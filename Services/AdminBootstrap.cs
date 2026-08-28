using ExamArchive.Data;
using ExamArchive.Models;
using Microsoft.EntityFrameworkCore;

namespace ExamArchive.Services;

/// <summary>
/// Makes sure the application always has an administrator who can sign in.
/// </summary>
/// <remarks>
/// Solves the problem that every other account is created by an admin, so the
/// first one cannot be. It cannot come from a migration either — migrations are
/// committed, and a password in the repository is a password everyone has.
/// <para>
/// This also serves as recovery, which is why the condition is "no <em>active</em>
/// admin" rather than "no users at all". An admin who is locked out, deactivated by
/// a colleague, or has simply forgotten their password can be restored by setting
/// two environment variables and restarting, instead of by editing the database
/// by hand.
/// </para>
/// </remarks>
public static class AdminBootstrap
{
    private const string UsernameKey = "Bootstrap:AdminUsername";
    private const string PasswordKey = "Bootstrap:AdminPassword";

    /// <summary>
    /// Creates or restores the administrator account described by configuration, if
    /// and only if no active administrator already exists.
    /// </summary>
    /// <remarks>
    /// Deliberately silent in the normal case. On a healthy system this runs on
    /// every start, finds an admin, and does nothing.
    /// </remarks>
    public static async Task EnsureAdminAsync(
        ExamArchiveDbContext db,
        UserAccountService accounts,
        IConfiguration configuration,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var hasActiveAdmin = await db.Users
            .AnyAsync(u => u.Role == UserRole.Admin && u.IsActive, cancellationToken);

        if (hasActiveAdmin)
        {
            return;
        }

        var username = configuration[UsernameKey];
        var password = configuration[PasswordKey];

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            // A warning rather than a crash. The public archive — browsing,
            // downloading, submitting — works perfectly well with nobody signed in,
            // so refusing to start would take a working site offline to complain
            // about a queue nobody can reach. The instructions are in the message
            // because this is exactly the moment somebody needs them.
            logger.LogWarning(
                "No active administrator exists and none is configured, so the moderation "
                + "queue cannot be reached. Set the {UsernameKey} and {PasswordKey} settings "
                + "and restart. As environment variables these are Bootstrap__AdminUsername "
                + "and Bootstrap__AdminPassword; never put them in appsettings.json, which is "
                + "committed to the repository.",
                UsernameKey,
                PasswordKey);

            return;
        }

        // The same rule the change-password endpoint applies. Shared rather than
        // repeated, because a policy written down twice is a policy that ends up
        // meaning two different things.
        if (password.Length < UserAccountService.MinimumPasswordLength)
        {
            // Refused rather than accepted-with-a-warning. This account has every
            // permission the system has, it is created unattended, and a weak
            // password here would likely outlive whoever chose it.
            logger.LogError(
                "The configured bootstrap password is too short: {Length} characters, "
                + "minimum {Minimum}. No administrator was created.",
                password.Length,
                UserAccountService.MinimumPasswordLength);

            return;
        }

        username = username.Trim();

        // Matched case-insensitively by the column's collation, so
        // "Admin" finds the account created as "admin" rather than colliding
        // with it on the unique index.
        var user = await db.Users
            .FirstOrDefaultAsync(u => u.Username == username, cancellationToken);

        var restored = user is not null;

        if (user is null)
        {
            user = new User { Username = username, CreatedAt = DateTime.UtcNow };
            db.Users.Add(user);
        }

        // Applied whether the account was found or created, because a deactivated
        // or demoted admin is the case this exists to repair. Anyone who can set
        // these variables already has the machine, and therefore the database file,
        // so this grants no access they could not take directly.
        user.Role = UserRole.Admin;
        user.IsActive = true;
        user.PasswordHash = accounts.HashPassword(user, password);

        await db.SaveChangesAsync(cancellationToken);

        // The username, never the password. This line ends up in log files that get
        // copied around, read over shoulders, and pasted into issue trackers.
        logger.LogWarning(
            restored
                ? "Restored administrator {Username} from configuration: the password was reset "
                  + "and the account re-enabled. Remove the bootstrap settings once you have "
                  + "signed in."
                : "Created the first administrator {Username} from configuration. Remove the "
                  + "bootstrap settings once you have signed in.",
            user.Username);
    }
}
