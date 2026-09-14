using ExamArchive.Data;
using ExamArchive.Dtos;
using ExamArchive.Models;
using ExamArchive.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ExamArchive.Controllers;

// Administrator management of staff accounts. Every action here is an
// administrator's, so the policy sits on the class where nothing can forget it.
//
// Accounts are never deleted: deactivation is reversible and keeps a moderator's
// past decisions attributable.
[ApiController]
[Route("api/users")]
[Produces("application/json")]
[Authorize(Policy = RolePolicies.Administrators)]
public class AdminUsersController : ControllerBase
{
    private readonly ExamArchiveDbContext _db;
    private readonly UserAccountService _accounts;
    private readonly ILogger<AdminUsersController> _logger;

    public AdminUsersController(
        ExamArchiveDbContext db,
        UserAccountService accounts,
        ILogger<AdminUsersController> logger)
    {
        _db = db;
        _accounts = accounts;
        _logger = logger;
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<UserSummaryDto>>> GetUsers(
        [FromQuery] PageRequest paging,
        CancellationToken cancellationToken)
    {
        // Deactivated accounts are included: they are the ones an admin is most
        // likely to be looking for. Username is unique, so ordering by it alone is
        // already the total order paging needs.
        var users = await _db.Users
            .AsNoTracking()
            .OrderBy(u => u.Username)
            .Select(u => new UserSummaryDto(
                u.Id, u.Username, u.Role, u.IsActive, u.MustChangePassword, u.CreatedAt))
            .ToPagedResultAsync(paging, cancellationToken);

        return Ok(users);
    }

    // The password is generated, never chosen by the admin, and the account must
    // replace it before it can do anything — so the admin knows it only until the
    // owner signs in once.
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UserCredentialDto>> CreateUser(
        [FromBody] CreateUserRequest request,
        CancellationToken cancellationToken)
    {
        if (RequireSuperAdminFor(request.Role) is { } forbidden)
        {
            return forbidden;
        }

        var username = request.Username.Trim();

        // Checked here for a decent error message; the unique index is what actually
        // guarantees it. Only this check would leave a race, only the index would
        // surface as a 500.
        var taken = await _db.Users
            .AnyAsync(u => u.Username == username, cancellationToken);

        if (taken)
        {
            return Problem(
                title: "Username taken",
                detail: $"An account named '{username}' already exists. Usernames are "
                    + "compared without regard to case.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var temporaryPassword = UserAccountService.GenerateTemporaryPassword();

        var user = new User
        {
            Username = username,
            Role = request.Role,
            IsActive = true,
            MustChangePassword = true,
            CreatedAt = DateTime.UtcNow
        };

        user.PasswordHash = _accounts.HashPassword(user, temporaryPassword);

        _db.Users.Add(user);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "{Admin} created the {Role} account {Username}.",
            User.Identity?.Name, user.Role, user.Username);

        return CreatedAtAction(
            nameof(GetUsers),
            new UserCredentialDto(user.Id, user.Username, user.Role, temporaryPassword));
    }

    // The whole of password recovery, replacing a "forgot password" flow on purpose:
    // that would need an email address per moderator and would make the university
    // mailbox the real credential for the archive.
    //
    // KNOWN LIMITATION: a session already signed in under the old password stays
    // valid until it expires.
    [HttpPost("{id:int}/reset-password")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserCredentialDto>> ResetPassword(
        int id,
        CancellationToken cancellationToken)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

        if (user is null)
        {
            return NotFound();
        }

        // An issued password is one the issuer knows until it is replaced, so
        // resetting an administrator's is indistinguishable from taking their account.
        if (RequireSuperAdminFor(user.Role) is { } forbidden)
        {
            return forbidden;
        }

        var temporaryPassword = UserAccountService.GenerateTemporaryPassword();

        user.PasswordHash = _accounts.HashPassword(user, temporaryPassword);
        user.MustChangePassword = true;

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogWarning(
            "{Admin} reset the password for {Username}.", User.Identity?.Name, user.Username);

        return Ok(new UserCredentialDto(user.Id, user.Username, user.Role, temporaryPassword));
    }

    [HttpPost("{id:int}/deactivate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UserSummaryDto>> Deactivate(
        int id,
        CancellationToken cancellationToken)
    {
        // Spans the last-admin check and the write it guards. Returning early rolls
        // back, which is why the conflict paths below simply return.
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

        if (user is null)
        {
            return NotFound();
        }

        if (RequireSuperAdminFor(user.Role) is { } forbidden)
        {
            return forbidden;
        }

        if (user.IsActive && await WouldRemoveLastAdminAsync(user, cancellationToken))
        {
            return LastAdminConflict("deactivated");
        }

        user.IsActive = false;
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogWarning(
            "{Admin} deactivated {Username}.", User.Identity?.Name, user.Username);

        return Ok(Summarize(user));
    }

    // The password is untouched, so somebody returning from leave signs in with what
    // they had. Reactivation and recovery are different events.
    [HttpPost("{id:int}/activate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserSummaryDto>> Activate(
        int id,
        CancellationToken cancellationToken)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

        if (user is null)
        {
            return NotFound();
        }

        // Symmetrical with deactivation: an admin who could not switch a colleague
        // off must not be able to switch one back on either.
        if (RequireSuperAdminFor(user.Role) is { } forbidden)
        {
            return forbidden;
        }

        user.IsActive = true;
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "{Admin} reactivated {Username}.", User.Identity?.Name, user.Username);

        return Ok(Summarize(user));
    }

    // A role change does not reach a cookie that has already been issued, so a
    // promotion takes effect at the user's next sign-in.
    [HttpPost("{id:int}/role")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UserSummaryDto>> ChangeRole(
        int id,
        [FromBody] ChangeRoleRequest request,
        CancellationToken cancellationToken)
    {
        // Same reasoning as Deactivate: the check and the write it guards belong to
        // one transaction.
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

        if (user is null)
        {
            return NotFound();
        }

        if (user.Role == request.Role)
        {
            return Ok(Summarize(user));
        }

        // Both ends: promoting into the tier and demoting out of it are equally the
        // super administrator's call.
        if (RequireSuperAdminFor(user.Role, request.Role) is { } forbidden)
        {
            return forbidden;
        }

        if (!RolePolicies.IsAdministrative(request.Role)
            && await WouldRemoveLastAdminAsync(user, cancellationToken))
        {
            return LastAdminConflict("demoted");
        }

        var previous = user.Role;
        user.Role = request.Role;
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogWarning(
            "{Admin} changed {Username} from {Previous} to {Role}.",
            User.Identity?.Name, user.Username, previous, user.Role);

        return Ok(Summarize(user));
    }

    // Protects the invariant that somebody can always manage the system: an admin who
    // demotes themselves while nobody else holds the role locks everyone out, and
    // recovery is then the bootstrap environment variables and a restart.
    private async Task<bool> WouldRemoveLastAdminAsync(User user, CancellationToken cancellationToken)
    {
        if (!RolePolicies.IsAdministrative(user.Role) || !user.IsActive)
        {
            return false;
        }

        // FOR UPDATE, so the rows this decision rests on cannot change between the
        // read and the write that follows. Both must be inside one transaction for
        // that to hold, which is why every caller opens one.
        //
        // Both administrative roles count: the invariant is that somebody can still
        // manage accounts, not that a particular role survives.
        //
        // Every active administrator is locked, not just the others, and always in
        // the same order — locking only the others would let two admins demoting each
        // other deadlock.
        //
        // Counted in memory because COUNT(*) over a locking read is not portable, and
        // this set is only ever a handful of rows.
        var activeAdmins = await _db.Users
            .FromSql($"SELECT * FROM Users WHERE Role IN (1, 2) AND IsActive = TRUE ORDER BY Id FOR UPDATE")
            .ToListAsync(cancellationToken);

        return activeAdmins.TrueForAll(admin => admin.Id == user.Id);
    }

    // The whole reason SuperAdmin exists: without it any administrator can create,
    // demote, deactivate or take over the password of any other, so the tier has no
    // owner. Applied to both ends of a role change.
    //
    // 403 rather than 404: the caller is a legitimate administrator who reached a
    // real account.
    private ObjectResult? RequireSuperAdminFor(params UserRole[] roles)
    {
        if (!roles.Any(RolePolicies.IsAdministrative)
            || User.GetRole() == UserRole.SuperAdmin)
        {
            return null;
        }

        return Problem(
            title: "Super administrator required",
            detail: "Only a super administrator may create, promote, demote, "
                + "deactivate or reset the password of an administrator.",
            statusCode: StatusCodes.Status403Forbidden);
    }

    private ObjectResult LastAdminConflict(string action) =>
        Problem(
            title: "Last administrator",
            detail: $"This is the only active administrator, so it cannot be {action}. "
                + "Promote another account first.",
            statusCode: StatusCodes.Status409Conflict);

    private static UserSummaryDto Summarize(User user) => new(
        user.Id, user.Username, user.Role, user.IsActive, user.MustChangePassword, user.CreatedAt);
}
