namespace ExamArchive.Models;

/// <summary>
/// An account that can sign in.
/// </summary>
/// <remarks>
/// Students and staff alike. Browsing stays public and needs no account, but
/// uploading no longer does: a submission that nobody can be held to invites spam
/// and misleading files, so a paper now arrives attached to whoever sent it.
/// <para>
/// Hand-rolled rather than ASP.NET Core Identity. Identity brings seven tables,
/// two-factor, lockout, external logins, email confirmation and a whole schema
/// this application would carry without using — and the piece actually worth
/// having, its password hasher, is a standalone class that works perfectly well
/// on its own. See <see cref="Services.UserAccountService"/>.
/// </para>
/// <para>
/// There is no email address and no personal name. The archive never needs to
/// contact anyone, so storing a way to would be collecting personal data for no
/// purpose. Adding it later is one nullable column; un-collecting it is not.
/// </para>
/// </remarks>
public class User
{
    public int Id { get; set; }

    /// <summary>
    /// The login name. Unique, and compared case-insensitively so that a student
    /// typing "Marko" reaches the account created as "marko".
    /// </summary>
    /// <remarks>
    /// The case-insensitivity is a collation on the column rather than a second
    /// normalized copy of the string, which is how Identity does it. On SQLite that
    /// makes the unique index case-insensitive too, so the database refuses a
    /// second "MARKO" instead of trusting the application to check.
    /// </remarks>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// The password, hashed. Never the password itself, and never reversible.
    /// </summary>
    /// <remarks>
    /// Format is whatever <c>PasswordHasher&lt;User&gt;</c> produces: a version
    /// marker, the salt, and the PBKDF2 output, all in one base64 string. The
    /// version marker is what lets the iteration count be raised later without
    /// invalidating existing passwords.
    /// </remarks>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>
    /// Defaults to the least privileged of the three, so an account created without
    /// an explicit role cannot come out as staff by omission.
    /// </summary>
    /// <remarks>
    /// This is belt and braces with the declaration order in <see cref="UserRole"/>:
    /// that makes a missing value on the wire bind to <c>User</c>, and this makes a
    /// <c>new User()</c> in code do the same. Both matter now that registration
    /// creates accounts on a path no administrator reviews.
    /// </remarks>
    public UserRole Role { get; set; } = UserRole.User;

    /// <summary>
    /// Whether the account may sign in. Checked at login, so revoking access is a
    /// flag rather than a delete.
    /// </summary>
    /// <remarks>
    /// Deleting is the wrong tool: a submitter's papers reference their account, and
    /// a moderator who leaves should stop being able to sign in without their past
    /// decisions becoming untraceable.
    /// </remarks>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Set when an administrator issued this account's current password, and
    /// cleared as soon as the owner replaces it.
    /// </summary>
    /// <remarks>
    /// While it is set the account can sign in and do nothing else. That is what
    /// makes an issued password genuinely temporary: without it, the password the
    /// admin chose stays in use indefinitely and the admin can sign in as that
    /// person whenever they like, which would make every decision in the moderation
    /// log deniable.
    /// <para>
    /// A flag nobody checks is decoration, so the enforcement lives in middleware
    /// that refuses every request except the few needed to get out of this state.
    /// </para>
    /// </remarks>
    public bool MustChangePassword { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Papers this account published directly through the staff upload endpoint.
    /// Empty for staff who only review the queue.
    /// </summary>
    public List<Paper> Papers { get; set; } = [];
}
