using ExamArchive.Data;
using ExamArchive.Dtos;
using ExamArchive.Models;
using ExamArchive.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ExamArchive.Controllers;

/// <summary>
/// Administrator management of staff accounts.
/// </summary>
/// <remarks>
/// Separate from <see cref="ModerationController"/> because the roles that may
/// reach them differ — a moderator judges papers, an administrator decides who may
/// judge papers. One controller with mixed attributes would put that distinction in
/// a place it is easy to get wrong.
/// <para>
/// Accounts are never deleted here. Deactivation is reversible and keeps a
/// moderator's past decisions attributable; deleting the row would strip their name
/// off everything they ever approved.
/// </para>
/// </remarks>
[ApiController]
[Route("api/admin/users")]
[Produces("application/json")]
[Authorize(Roles = nameof(UserRole.Admin))]
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

    /// <summary>Lists every staff account, active or not.</summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<UserSummaryDto>>> GetUsers(
        CancellationToken cancellationToken)
    {
        // Deactivated accounts are included: they are the ones an admin is most
        // likely to be looking for, either to reinstate somebody or to check that a
        // departure was actually processed.
        var users = await _db.Users
            .AsNoTracking()
            .OrderBy(u => u.Username)
            .Select(u => new UserSummaryDto(
                u.Id, u.Username, u.Role, u.IsActive, u.MustChangePassword, u.CreatedAt))
            .ToListAsync(cancellationToken);

        return Ok(users);
    }

    /// <summary>
    /// Creates a staff account and returns the password to hand over.
    /// </summary>
    /// <remarks>
    /// The password is generated, never chosen by the admin, and the account must
    /// replace it before it can do anything. So the admin knows the password only
    /// for as long as it takes the owner to sign in once — after which nobody but
    /// the owner does, and a decision in the moderation log genuinely belongs to the
    /// person named on it.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UserCredentialDto>> CreateUser(
        [FromBody] CreateUserRequest request,
        CancellationToken cancellationToken)
    {
        var username = request.Username.Trim();

        // Checked here for a decent error message; the unique index is what actually
        // guarantees it. Doing only this check would leave a race, and doing only
        // the index would surface as a 500.
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

    /// <summary>
    /// Issues a new temporary password, for somebody who has forgotten theirs.
    /// </summary>
    /// <remarks>
    /// This is the whole of password recovery, and it replaces a "forgot password"
    /// flow on purpose. That flow would need an email address per moderator, a mail
    /// server, and a token mechanism — and it would make the university mailbox the
    /// real credential for the archive. Here the admin verifies identity by
    /// recognising a colleague, which for a dozen people in one building is stronger
    /// than a link in an inbox.
    /// <para>
    /// KNOWN LIMITATION: a session already signed in under the old password stays
    /// valid until it expires. Resetting the password of an account that has been
    /// compromised does not yet evict whoever compromised it.
    /// </para>
    /// </remarks>
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

        var temporaryPassword = UserAccountService.GenerateTemporaryPassword();

        user.PasswordHash = _accounts.HashPassword(user, temporaryPassword);
        user.MustChangePassword = true;

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogWarning(
            "{Admin} reset the password for {Username}.", User.Identity?.Name, user.Username);

        return Ok(new UserCredentialDto(user.Id, user.Username, user.Role, temporaryPassword));
    }

    /// <summary>Revokes an account's ability to sign in, reversibly.</summary>
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

    /// <summary>Restores a deactivated account.</summary>
    /// <remarks>
    /// The password is untouched, so somebody returning from leave signs in with
    /// what they had. If they have forgotten it, reset it separately — reactivation
    /// and recovery are different events and rolling them together would issue a new
    /// password to people who did not need one.
    /// </remarks>
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

        user.IsActive = true;
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "{Admin} reactivated {Username}.", User.Identity?.Name, user.Username);

        return Ok(Summarize(user));
    }

    /// <summary>
    /// Promotes a moderator to administrator, or demotes one.
    /// </summary>
    /// <remarks>
    /// Promotion is how the archive stops depending on a single administrator, which
    /// is worth doing early: with only one, a forgotten password means recovering
    /// through the bootstrap variables and a restart.
    /// <para>
    /// A role change does not reach a cookie that has already been issued, so a
    /// promotion takes effect at the user's next sign-in.
    /// </para>
    /// </remarks>
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

        if (request.Role != UserRole.Admin
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

    /// <summary>
    /// Whether removing this account's administrator access would leave none.
    /// </summary>
    /// <remarks>
    /// The invariant this protects is that somebody can always manage the system. An
    /// admin who demotes themselves while nobody else holds the role locks everyone
    /// out permanently, recoverable only through the bootstrap environment variables
    /// and a restart.
    /// <para>
    /// Reading the count and then acting on it would be a race — two admins demoting
    /// each other at once would each see the other and each proceed, leaving none.
    /// The read therefore locks the rows it counts and shares a transaction with the
    /// write, so the second request waits for the first to commit and then sees the
    /// world it actually created.
    /// </para>
    /// </remarks>
    private async Task<bool> WouldRemoveLastAdminAsync(User user, CancellationToken cancellationToken)
    {
        if (user.Role != UserRole.Admin || !user.IsActive)
        {
            return false;
        }

        // FOR UPDATE, so the rows this decision rests on cannot change between the
        // read and the write that follows it. Both must be inside one transaction
        // for that to hold, which is why every caller opens one.
        //
        // Every active admin is locked, not just the others, and always in the same
        // order. Locking only the others would have two admins demoting each other
        // take each other's row and deadlock; locking the whole set in a fixed order
        // makes the second request wait, then re-read a world where the first has
        // already committed and correctly refuse.
        //
        // Counted in memory rather than by the database because COUNT(*) over a
        // locking read is not portable, and this set is only ever a handful of rows.
        var activeAdmins = await _db.Users
            .FromSql($"SELECT * FROM Users WHERE Role = 'Admin' AND IsActive = TRUE ORDER BY Id FOR UPDATE")
            .ToListAsync(cancellationToken);

        return activeAdmins.TrueForAll(admin => admin.Id == user.Id);
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
