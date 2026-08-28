using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ExamArchive.Data;
using ExamArchive.Models;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ExamArchive.Services;

/// <summary>
/// Hashes passwords, checks them, and turns an account into the identity the rest
/// of the framework understands.
/// </summary>
/// <remarks>
/// The hashing is <see cref="PasswordHasher{TUser}"/> straight out of ASP.NET Core
/// Identity, used without the rest of Identity. It is PBKDF2-HMAC-SHA512 with a
/// per-password random salt and a high iteration count, and it writes a version
/// marker into every hash so the iteration count can be raised later without
/// invalidating anybody's password — see <see cref="ValidateCredentialsAsync"/>.
/// <para>
/// Writing this by hand is the classic way to get it wrong: a bare SHA-256, no
/// salt, or a comparison with <c>==</c> that leaks the answer through its timing.
/// None of that is worth reinventing for a class that ships in the framework.
/// </para>
/// </remarks>
public sealed class UserAccountService
{
    /// <summary>
    /// A real hash, verified against when no such user exists.
    /// </summary>
    /// <remarks>
    /// Without this, a login for an unknown username returns as fast as the
    /// database lookup, while a known username costs the full PBKDF2 work. That
    /// difference is measurable over a network and turns the login endpoint into a
    /// way to enumerate valid accounts. Verifying a throwaway hash makes both paths
    /// do the same work.
    /// </remarks>
    private static readonly string DecoyHash =
        new PasswordHasher<User>().HashPassword(new User(), "not-a-real-password");

    /// <summary>
    /// Shortest password this application will set. The one place the rule lives.
    /// </summary>
    /// <remarks>
    /// Length only, with no demand for punctuation or mixed case. Composition rules
    /// steer people towards a small set of predictable shapes — a capital at the
    /// front, a digit and a bang at the end — which barely enlarges the search space
    /// while making the result harder to remember. Length is what actually costs an
    /// attacker.
    /// <para>
    /// Enforced wherever a password is <em>set</em>, and deliberately not at sign-in:
    /// repeating the rule on the login form would tell an attacker the shape of the
    /// search space for free, and an existing password shorter than this must still
    /// be able to get its owner in so they can change it.
    /// </para>
    /// </remarks>
    public const int MinimumPasswordLength = 12;

    /// <summary>
    /// Claim recording that the account is on an administrator-issued password.
    /// </summary>
    /// <remarks>
    /// Carried in the cookie rather than read from the database on every request,
    /// which is why the change-password endpoint re-issues the cookie the moment the
    /// password is changed. Without that the claim would outlive the condition and
    /// the account would stay locked out of everything until the cookie expired.
    /// </remarks>
    public const string MustChangePasswordClaim = "exam-archive:must-change-password";

    /// <summary>
    /// Alphabet for generated passwords: digits and uppercase letters, minus the
    /// pairs that look alike in most fonts.
    /// </summary>
    /// <remarks>
    /// Deliberately the same idea as <see cref="ClaimToken"/> and deliberately not
    /// the same code. The two have different rules — a claim code has no length
    /// policy to satisfy and lives for months, a temporary password must clear
    /// <see cref="MinimumPasswordLength"/> and should be discarded within minutes —
    /// so sharing an implementation would mean one change quietly altering the other.
    /// </remarks>
    private const string TemporaryPasswordAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private readonly ExamArchiveDbContext _db;
    private readonly PasswordHasher<User> _hasher = new();
    private readonly ILogger<UserAccountService> _logger;

    public UserAccountService(ExamArchiveDbContext db, ILogger<UserAccountService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>Produces the stored form of a password.</summary>
    public string HashPassword(User user, string password) =>
        _hasher.HashPassword(user, password);

    /// <summary>
    /// Builds a password for an administrator to hand over, in the form
    /// <c>H7K2-9MQX-BD3F-WY6N</c>.
    /// </summary>
    /// <remarks>
    /// Generated rather than chosen by the admin, so the admin never learns a
    /// password the account keeps. They read this one out once, the owner replaces
    /// it on first sign-in, and from then on nobody but the owner knows it.
    /// <para>
    /// Sixteen symbols from a 32-letter alphabet is 80 bits — far more than the
    /// policy minimum, which costs nothing because it is typed once and thrown
    /// away. Reducing each byte modulo 32 is unbiased only because 256 divides
    /// evenly by 32.
    /// </para>
    /// </remarks>
    public static string GenerateTemporaryPassword()
    {
        const int length = 16;
        const int groupSize = 4;

        var bytes = RandomNumberGenerator.GetBytes(length);
        var password = new StringBuilder(length + (length / groupSize) - 1);

        for (var i = 0; i < length; i++)
        {
            if (i > 0 && i % groupSize == 0)
            {
                password.Append('-');
            }

            password.Append(TemporaryPasswordAlphabet[bytes[i] % TemporaryPasswordAlphabet.Length]);
        }

        return password.ToString();
    }

    /// <summary>
    /// Checks a username and password, returning the account on success and null
    /// on any failure.
    /// </summary>
    /// <remarks>
    /// One null for every reason — no such user, wrong password, deactivated
    /// account — because the caller turns it into one message. Telling an
    /// unauthenticated caller which of those it was hands them a way to confirm
    /// that an account exists.
    /// </remarks>
    public async Task<User?> ValidateCredentialsAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        // Tracked, not AsNoTracking: a successful login may rewrite the hash below.
        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.Username == username, cancellationToken);

        if (user is null)
        {
            // Burn the same CPU the real path would, then fail. The result is
            // discarded; the point is the time it took.
            _hasher.VerifyHashedPassword(new User(), DecoyHash, password);
            return null;
        }

        var result = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);

        if (result == PasswordVerificationResult.Failed)
        {
            return null;
        }

        // Checked after the password, not before. Short-circuiting on a disabled
        // account would answer faster than a wrong password does, which is the same
        // timing leak the decoy above exists to close.
        if (!user.IsActive)
        {
            _logger.LogWarning(
                "Sign-in refused for deactivated account {Username}.", user.Username);
            return null;
        }

        // The password is right but was hashed by an older, weaker configuration.
        // This is the moment it can be upgraded: the plaintext is in hand exactly
        // once, here. Doing nothing would leave old accounts on old parameters
        // forever.
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = _hasher.HashPassword(user, password);
            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Upgraded the stored password hash for {Username}.", user.Username);
        }

        return user;
    }

    /// <summary>
    /// Why a password change was refused, or that it succeeded.
    /// </summary>
    /// <remarks>
    /// Distinguished, unlike the single null from <see cref="ValidateCredentialsAsync"/>.
    /// The caller here is already signed in and the server knows exactly who they
    /// are, so there is no identity left to leak — and a person changing their own
    /// password needs to be told which of these went wrong in order to fix it.
    /// </remarks>
    public enum PasswordChangeResult
    {
        Changed,

        /// <summary>The account vanished between the cookie being issued and now.</summary>
        UserNotFound,

        /// <summary>The account was deactivated while its session was still live.</summary>
        AccountInactive,

        /// <summary>The current password was wrong, so this is not the account's owner.</summary>
        IncorrectPassword,

        /// <summary>The new password is the old one, so nothing would change.</summary>
        SameAsCurrent,

        /// <summary>The new password is shorter than <see cref="MinimumPasswordLength"/>.</summary>
        TooShort
    }

    /// <summary>
    /// Replaces a signed-in account's own password.
    /// </summary>
    /// <remarks>
    /// The current password is required, and that is the point of the endpoint
    /// rather than an inconvenience. A session cookie proves that this browser was
    /// signed in at some point, not that the person at the keyboard owns the
    /// account — and an unlocked machine in a faculty office is the realistic way
    /// that gap gets exploited. Knowing the current password is what distinguishes
    /// the owner from a passer-by.
    /// <para>
    /// KNOWN LIMITATION: changing a password does not end sessions on other devices.
    /// Cookies are self-contained, so one already issued stays valid until it
    /// expires — including one held by whoever prompted the change. Closing that
    /// requires a value on the account that is checked per request and rotated here;
    /// until then, a compromised session survives its own password reset.
    /// </para>
    /// </remarks>
    public async Task<PasswordChangeResult> ChangePasswordAsync(
        int userId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken)
    {
        // Checked here rather than trusted from the request, so the rule holds even
        // if a caller reaches this without model validation having run.
        if (newPassword.Length < MinimumPasswordLength)
        {
            return PasswordChangeResult.TooShort;
        }

        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null)
        {
            return PasswordChangeResult.UserNotFound;
        }

        // The first point in the request pipeline where IsActive is re-read for an
        // already-signed-in caller. Everywhere else still trusts the cookie, which
        // is why revoking an account currently takes effect at its expiry rather
        // than immediately.
        if (!user.IsActive)
        {
            return PasswordChangeResult.AccountInactive;
        }

        if (_hasher.VerifyHashedPassword(user, user.PasswordHash, currentPassword)
            == PasswordVerificationResult.Failed)
        {
            _logger.LogWarning(
                "Password change refused for {Username}: the current password was wrong.",
                user.Username);

            return PasswordChangeResult.IncorrectPassword;
        }

        // Caught so that someone who believes they have changed their password
        // actually has. This matters most after a temporary password is issued,
        // where retyping the temporary one would otherwise look like success.
        if (_hasher.VerifyHashedPassword(user, user.PasswordHash, newPassword)
            != PasswordVerificationResult.Failed)
        {
            return PasswordChangeResult.SameAsCurrent;
        }

        user.PasswordHash = _hasher.HashPassword(user, newPassword);

        // The account is now on a password only its owner knows, which is exactly
        // the condition the flag was tracking.
        user.MustChangePassword = false;

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("{Username} changed their password.", user.Username);

        return PasswordChangeResult.Changed;
    }

    /// <summary>
    /// Reads an account by id, for re-issuing a cookie after its claims have gone
    /// stale.
    /// </summary>
    public async Task<User?> FindByIdAsync(int id, CancellationToken cancellationToken) =>
        await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    /// <summary>
    /// Builds the identity that goes into the session cookie.
    /// </summary>
    /// <remarks>
    /// Only the id, the name and the role. Everything placed here is copied into
    /// the cookie and travels on every request, so it should be the few facts worth
    /// re-reading on each call rather than a snapshot of the account.
    /// <para>
    /// It is also a snapshot in the way that matters: a role changed in the database
    /// does not reach a cookie already issued. For an archive with a handful of
    /// staff that is fine — demoting someone takes effect when their cookie expires
    /// or they sign out. An application where that is not acceptable has to validate
    /// the principal against the database on each request.
    /// </para>
    /// </remarks>
    public static ClaimsPrincipal BuildPrincipal(User user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            // The number, not the name. A claim value can only be a string, so it
            // travels as text — but it is the same text the policies compare, and
            // ClaimsPrincipalExtensions.GetRole is the only thing that reads it.
            new(ClaimTypes.Role, ((int)user.Role).ToString(CultureInfo.InvariantCulture))
        };

        // Added only when true, so the absence of the claim is the normal state and
        // an old cookie issued before this existed reads as "nothing pending"
        // rather than locking its owner out of the application.
        if (user.MustChangePassword)
        {
            claims.Add(new Claim(MustChangePasswordClaim, "true"));
        }

        // The scheme name has to match the one the cookie handler was registered
        // under, or the resulting principal is not considered authenticated.
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

        return new ClaimsPrincipal(identity);
    }
}
