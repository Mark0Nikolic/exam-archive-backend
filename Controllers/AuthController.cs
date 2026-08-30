using System.Security.Claims;
using ExamArchive.Data;
using ExamArchive.Dtos;
using ExamArchive.Models;
using ExamArchive.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ExamArchive.Controllers;

/// <summary>
/// Signing in and out, and reporting who the caller is.
/// </summary>
/// <remarks>
/// Sessions are cookies, not bearer tokens. The deciding property is that a cookie
/// marked HttpOnly cannot be read by JavaScript, so a cross-site scripting flaw
/// anywhere in the frontend cannot walk off with a moderator's session — and this
/// application serves unreviewed, submitter-supplied files, which is a larger XSS
/// surface than most. A JWT held in localStorage is readable by any script on the
/// page by design.
/// <para>
/// The cost is that cookies are attached by the browser automatically, which is
/// what CSRF exploits; that is handled by the SameSite attribute set in Program.cs
/// and by the fact that every state-changing endpoint is a POST that will not
/// accept a form content type.
/// </para>
/// </remarks>
[ApiController]
[Route("api")]
[Produces("application/json")]
public class AuthController : ControllerBase
{
    private readonly ExamArchiveDbContext _db;
    private readonly UserAccountService _accounts;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        ExamArchiveDbContext db,
        UserAccountService accounts,
        ILogger<AuthController> logger)
    {
        _db = db;
        _accounts = accounts;
        _logger = logger;
    }

    /// <summary>
    /// Creates an account and signs it straight in.
    /// </summary>
    /// <remarks>
    /// Submitting used to be anonymous, which meant an open endpoint accepted spam
    /// and misleading files as readily as exam papers and left a moderator to sort
    /// them by hand. An account is the cheapest thing that makes a pattern of bad
    /// submissions answerable: it does not prove who anybody is, but it gives
    /// repeated abuse a single thing to deactivate.
    /// <para>
    /// The role is fixed here rather than taken from the request. Registration is
    /// the one account-creating path with no administrator behind it, so the caller
    /// naming their own role is the difference between an archive and a free-for-all.
    /// </para>
    /// <para>
    /// Signed in on success, because requiring a separate login immediately after
    /// choosing a password is a step that exists only to be annoying.
    /// </para>
    /// </remarks>
    [HttpPost("register")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CurrentUserDto>> Register(
        [FromBody] RegisterRequest request,
        CancellationToken cancellationToken)
    {
        var username = request.Username.Trim();

        // Checked for a usable message rather than for correctness — the unique
        // index is what actually guarantees it, and the catch below is what turns
        // the losing side of a race into a 409 instead of a 500. This endpoint is
        // reachable by anyone, so that race is worth handling rather than noting.
        var taken = await _db.Users
            .AnyAsync(u => u.Username == username, cancellationToken);

        if (taken)
        {
            return UsernameTaken(username);
        }

        var user = new User
        {
            Username = username,

            // Stated rather than left to the property initialiser. Both say User
            // today; only one of them is a decision about what registration grants.
            Role = UserRole.User,
            IsActive = true,
            MustChangePassword = false,
            CreatedAt = DateTime.UtcNow
        };

        user.PasswordHash = _accounts.HashPassword(user, request.Password);

        _db.Users.Add(user);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The only unique constraint on this table is the username, so this is
            // the other half of the race above: someone claimed the name between
            // the check and the insert.
            return UsernameTaken(username);
        }

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            UserAccountService.BuildPrincipal(user),
            new AuthenticationProperties { IsPersistent = true });

        _logger.LogInformation("{Username} registered.", user.Username);

        return CreatedAtAction(
            nameof(Me),
            new CurrentUserDto(user.Id, user.Username, user.Role));
    }

    private ObjectResult UsernameTaken(string username) =>
        Problem(
            title: "Username taken",
            detail: $"An account named '{username}' already exists. Usernames are "
                + "compared without regard to case.",
            statusCode: StatusCodes.Status409Conflict);

    /// <summary>
    /// Signs in and issues the session cookie.
    /// </summary>
    /// <remarks>
    /// A frontend must send this with credentials included, and every later request
    /// too, or the browser will hold the cookie and never present it.
    /// </remarks>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<CurrentUserDto>> Login(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken)
    {
        var user = await _accounts.ValidateCredentialsAsync(
            request.Username, request.Password, cancellationToken);

        if (user is null)
        {
            // One message for every failure. "No such user" and "wrong password"
            // are different facts, and telling them apart is how an attacker builds
            // a list of real accounts before spending any effort on passwords.
            _logger.LogInformation(
                "Failed sign-in attempt for {Username}.", request.Username);

            return Problem(
                title: "Sign-in failed",
                detail: "The username or password is incorrect.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            UserAccountService.BuildPrincipal(user),
            new AuthenticationProperties
            {
                // Survives closing the browser. A moderator working through a queue
                // over several days should not have to sign in each morning, and the
                // sliding expiration configured on the handler ends the session on
                // inactivity rather than on a fixed clock.
                IsPersistent = true
            });

        _logger.LogInformation(
            "{Username} signed in as {Role}.", user.Username, user.Role);

        return Ok(new CurrentUserDto(user.Id, user.Username, user.Role));
    }

    /// <summary>
    /// Signs out, clearing the session cookie.
    /// </summary>
    /// <remarks>
    /// Anonymous rather than [Authorize] on purpose: signing out a session that has
    /// already expired should quietly succeed, not answer 401 and leave a frontend
    /// deciding what to do about a failure that does not matter.
    /// </remarks>
    [HttpPost("logout")]
    [AllowAnonymous]
    [AllowsPendingPasswordChange]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        return NoContent();
    }

    /// <summary>
    /// Changes the signed-in account's own password.
    /// </summary>
    /// <remarks>
    /// Self-service, and the only way a password changes without an administrator
    /// involved. It is what makes an admin-issued temporary password temporary:
    /// without this, a password the admin chose stays in use forever and the admin
    /// can sign in as that person indefinitely.
    /// <para>
    /// Nobody can change anybody else's password here — the account is taken from
    /// the session cookie, not from the request body. An admin resetting somebody
    /// else's is a different operation on a different controller, and keeping them
    /// apart means this endpoint has no privilege to escalate.
    /// </para>
    /// </remarks>
    [HttpPost("change-password")]
    [Authorize]
    [AllowsPendingPasswordChange]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ChangePassword(
        [FromBody] ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        var id = User.GetUserId();

        if (id is null)
        {
            return Unauthorized();
        }

        var result = await _accounts.ChangePasswordAsync(
            id.Value, request.CurrentPassword, request.NewPassword, cancellationToken);

        switch (result)
        {
            case UserAccountService.PasswordChangeResult.Changed:
                // Re-issued because the cookie may carry the must-change claim, and
                // the account no longer owes anything. Skipping this would leave
                // them locked out of every endpoint by a claim describing a
                // condition they have just fixed.
                var user = await _accounts.FindByIdAsync(id.Value, cancellationToken);

                if (user is not null)
                {
                    await HttpContext.SignInAsync(
                        CookieAuthenticationDefaults.AuthenticationScheme,
                        UserAccountService.BuildPrincipal(user),
                        new AuthenticationProperties { IsPersistent = true });
                }

                return NoContent();

            // The cookie is valid but the account behind it is gone or switched off.
            // Signing them out turns a confusing failure into the correct state:
            // they are not signed in, and the next request says so.
            case UserAccountService.PasswordChangeResult.UserNotFound:
            case UserAccountService.PasswordChangeResult.AccountInactive:
                await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

                return Problem(
                    title: "Account unavailable",
                    detail: "This account is no longer active. Sign in again.",
                    statusCode: StatusCodes.Status403Forbidden);

            // Reported against the field so a form can mark the right box. Safe to
            // be specific: the caller is authenticated and already knows whose
            // account this is, so there is no existence to leak.
            case UserAccountService.PasswordChangeResult.IncorrectPassword:
                ModelState.AddModelError(
                    nameof(request.CurrentPassword), "That is not your current password.");
                break;

            case UserAccountService.PasswordChangeResult.SameAsCurrent:
                ModelState.AddModelError(
                    nameof(request.NewPassword),
                    "The new password must be different from the current one.");
                break;

            case UserAccountService.PasswordChangeResult.TooShort:
                ModelState.AddModelError(
                    nameof(request.NewPassword),
                    $"The new password must be at least {UserAccountService.MinimumPasswordLength} characters.");
                break;
        }

        return ValidationProblem(ModelState);
    }

    /// <summary>
    /// Reports the signed-in account, or 401 if there is none.
    /// </summary>
    /// <remarks>
    /// The cookie is HttpOnly, so a page that has just loaded cannot inspect it to
    /// find out whether it holds a session. This is how it asks.
    /// </remarks>
    [HttpGet("me")]
    [Authorize]
    [AllowsPendingPasswordChange]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<CurrentUserDto> Me()
    {
        var id = User.GetUserId();
        var role = User.GetRole();

        // [Authorize] only proves a principal exists, not that it carries the
        // claims this application put there. A cookie issued by an older version of
        // the sign-in code would satisfy it and still be missing one of these — or
        // carry a role name where a number is now expected — so both are checked
        // rather than dereferenced. Answering 401 tells the frontend to sign in
        // again, which reissues a cookie in the current shape.
        if (id is null || role is null)
        {
            return Unauthorized();
        }

        return Ok(new CurrentUserDto(
            id.Value,
            User.Identity?.Name ?? string.Empty,
            role.Value));
    }
}
