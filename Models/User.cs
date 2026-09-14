namespace ExamArchive.Models;

// Hand-rolled rather than ASP.NET Core Identity: only the password hasher was worth
// having, and it works standalone. See Services.UserAccountService.
//
// There is deliberately no email address or personal name — the archive never needs
// to contact anyone.
public class User
{
    public int Id { get; set; }

    // Unique, and compared case-insensitively via a _ci collation on the column, so
    // the unique index refuses a second "MARKO" too.
    public string Username { get; set; } = string.Empty;

    // Whatever PasswordHasher<User> produces: version marker, salt and PBKDF2 output
    // in one base64 string. The version marker is what lets the iteration count be
    // raised later without invalidating existing passwords.
    public string PasswordHash { get; set; } = string.Empty;

    // Defaults to the least privileged role so a `new User()` that forgets to assign
    // one produces a student rather than an unstorable zero.
    public UserRole Role { get; set; } = UserRole.User;

    // Checked at login, so revoking access is a flag rather than a delete — a
    // submitter's papers reference their account.
    public bool IsActive { get; set; } = true;

    // Set when an administrator issued this account's current password. While it is
    // set the account can sign in and do nothing else; the enforcement lives in
    // middleware.
    public bool MustChangePassword { get; set; }

    public DateTime CreatedAt { get; set; }

    public List<Paper> Papers { get; set; } = [];
}
