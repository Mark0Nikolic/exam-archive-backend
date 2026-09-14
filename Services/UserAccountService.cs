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

// Hashes passwords, checks them, and turns an account into the identity the rest of
// the framework understands. The hashing is PasswordHasher<TUser> out of ASP.NET
// Core Identity, used without the rest of Identity: PBKDF2-HMAC-SHA512, per-password
// salt, and a version marker in every hash so the iteration count can be raised
// later without invalidating anybody's password.
public sealed class UserAccountService
{
    // A real hash, verified against when no such user exists. Without it a login for
    // an unknown username returns as fast as the database lookup while a known one
    // costs the full PBKDF2 work — a measurable way to enumerate accounts.
    private static readonly string DecoyHash =
        new PasswordHasher<User>().HashPassword(new User(), "not-a-real-password");

    // Length only, with no composition rules. Enforced wherever a password is set,
    // and deliberately not at sign-in: an existing password shorter than this must
    // still get its owner in so they can change it.
    public const int MinimumPasswordLength = 12;

    // Carried in the cookie rather than read from the database on every request,
    // which is why the change-password endpoint re-issues the cookie immediately.
    public const string MustChangePasswordClaim = "exam-archive:must-change-password";

    // Same idea as ClaimToken and deliberately not the same code: the two have
    // different rules, so sharing an implementation would mean one change quietly
    // altering the other.
    private const string TemporaryPasswordAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private readonly ExamArchiveDbContext _db;
    private readonly PasswordHasher<User> _hasher = new();
    private readonly ILogger<UserAccountService> _logger;

    public UserAccountService(ExamArchiveDbContext db, ILogger<UserAccountService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public string HashPassword(User user, string password) =>
        _hasher.HashPassword(user, password);

    // A password for an administrator to hand over, in the form H7K2-9MQX-BD3F-WY6N.
    // Generated rather than chosen, so the admin never learns a password the account
    // keeps. Reducing each byte modulo 32 is unbiased only because 256 divides
    // evenly by 32.
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

    // One null for every reason — no such user, wrong password, deactivated account —
    // because telling an unauthenticated caller which hands them a way to confirm
    // that an account exists.
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

        // Checked after the password, not before: short-circuiting on a disabled
        // account would answer faster than a wrong password does.
        if (!user.IsActive)
        {
            _logger.LogWarning(
                "Sign-in refused for deactivated account {Username}.", user.Username);
            return null;
        }

        // The password is right but was hashed by an older configuration, and the
        // plaintext is in hand exactly once, here.
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = _hasher.HashPassword(user, password);
            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Upgraded the stored password hash for {Username}.", user.Username);
        }

        return user;
    }

    // Distinguished, unlike the single null from ValidateCredentialsAsync: the caller
    // is already signed in, so there is no identity left to leak.
    public enum PasswordChangeResult
    {
        Changed,

        UserNotFound,

        AccountInactive,

        IncorrectPassword,

        SameAsCurrent,

        TooShort
    }

    // The current password is required because a session cookie proves this browser
    // was signed in at some point, not that the person at the keyboard owns the
    // account.
    //
    // KNOWN LIMITATION: changing a password does not end sessions on other devices.
    // Cookies are self-contained, so one already issued stays valid until it expires.
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

        // The only place IsActive is re-read for an already-signed-in caller, which
        // is why revoking an account otherwise takes effect at cookie expiry.
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

        // Matters most after a temporary password is issued, where retyping the
        // temporary one would otherwise look like success.
        if (_hasher.VerifyHashedPassword(user, user.PasswordHash, newPassword)
            != PasswordVerificationResult.Failed)
        {
            return PasswordChangeResult.SameAsCurrent;
        }

        user.PasswordHash = _hasher.HashPassword(user, newPassword);

        user.MustChangePassword = false;

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("{Username} changed their password.", user.Username);

        return PasswordChangeResult.Changed;
    }

    public async Task<User?> FindByIdAsync(int id, CancellationToken cancellationToken) =>
        await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    // Only the id, the name and the role: everything here is copied into the cookie
    // and travels on every request. It is a snapshot — a role changed in the database
    // does not reach a cookie already issued.
    public static ClaimsPrincipal BuildPrincipal(User user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            // The number, not the name. ClaimsPrincipalExtensions.GetRole is the
            // only thing that reads it back.
            new(ClaimTypes.Role, ((int)user.Role).ToString(CultureInfo.InvariantCulture))
        };

        // Added only when true, so an old cookie issued before this existed reads as
        // "nothing pending" rather than locking its owner out.
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
