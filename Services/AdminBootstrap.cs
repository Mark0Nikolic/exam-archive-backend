using ExamArchive.Data;
using ExamArchive.Models;
using Microsoft.EntityFrameworkCore;

namespace ExamArchive.Services;

// Makes sure the application always has an administrator who can sign in: every
// other account is created by an admin, so the first one cannot be. The condition is
// "no active super admin" rather than "no users", so this also serves as recovery.
public static class AdminBootstrap
{
    private const string UsernameKey = "Bootstrap:AdminUsername";
    private const string PasswordKey = "Bootstrap:AdminPassword";

    public static async Task EnsureAdminAsync(
        ExamArchiveDbContext db,
        UserAccountService accounts,
        IConfiguration configuration,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var hasActiveAdmin = await db.Users
            .AnyAsync(u => u.Role == UserRole.SuperAdmin && u.IsActive, cancellationToken);

        if (hasActiveAdmin)
        {
            return;
        }

        var username = configuration[UsernameKey];
        var password = configuration[PasswordKey];

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            // A warning rather than a crash: the public archive works fine with
            // nobody signed in, so refusing to start would take a working site
            // offline to complain about a queue nobody can reach.
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

        if (password.Length < UserAccountService.MinimumPasswordLength)
        {
            // Refused rather than accepted with a warning: this account has every
            // permission there is and is created unattended.
            logger.LogError(
                "The configured bootstrap password is too short: {Length} characters, "
                + "minimum {Minimum}. No administrator was created.",
                password.Length,
                UserAccountService.MinimumPasswordLength);

            return;
        }

        username = username.Trim();

        var user = await db.Users
            .FirstOrDefaultAsync(u => u.Username == username, cancellationToken);

        var restored = user is not null;

        if (user is null)
        {
            user = new User { Username = username, CreatedAt = DateTime.UtcNow };
            db.Users.Add(user);
        }

        // Applied whether the account was found or created, because a deactivated or
        // demoted admin is the case this exists to repair.
        user.Role = UserRole.SuperAdmin;
        user.IsActive = true;
        user.PasswordHash = accounts.HashPassword(user, password);

        await db.SaveChangesAsync(cancellationToken);

        // The username, never the password: this line ends up in log files.
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
