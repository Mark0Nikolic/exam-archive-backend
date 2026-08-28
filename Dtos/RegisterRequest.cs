using System.ComponentModel.DataAnnotations;
using ExamArchive.Services;

namespace ExamArchive.Dtos;

/// <summary>
/// Someone creating their own account in order to submit papers.
/// </summary>
/// <remarks>
/// Deliberately not <see cref="CreateUserRequest"/> with the role left off. That
/// DTO carries a role because an administrator chooses one; this one must not,
/// because the choice is not the caller's to make. Sharing the type and ignoring
/// the field would leave a property on the wire that looks like it works, and one
/// careless bind away from letting anybody register as an administrator.
/// <para>
/// There is no email address, matching <see cref="Models.User"/>: nothing in the
/// archive contacts anybody, so collecting one would be gathering personal data
/// for no purpose. The cost is that a forgotten password needs an administrator
/// to reset it, which is the honest trade for not holding contact details.
/// </para>
/// </remarks>
public class RegisterRequest
{
    /// <summary>The login name to claim.</summary>
    /// <remarks>
    /// The same rule as <see cref="CreateUserRequest.Username"/>, and it has to
    /// stay that way: two spellings of what a username may contain would let a
    /// registration create a name no administrator could have created.
    /// </remarks>
    [Required]
    [StringLength(50, MinimumLength = 3)]
    [RegularExpression(
        "^[a-zA-Z0-9._-]+$",
        ErrorMessage = "A username may contain only letters, digits, dots, dashes and underscores.")]
    public string Username { get; set; } = string.Empty;

    /// <summary>The password to sign in with.</summary>
    /// <remarks>
    /// Held to the same minimum as every other place a password is set, from the
    /// one constant that defines it. Seeded development accounts are below this
    /// length and still work, because the rule is enforced where a password is
    /// chosen and deliberately not at sign-in.
    /// </remarks>
    [Required]
    [StringLength(
        128,
        MinimumLength = UserAccountService.MinimumPasswordLength,
        ErrorMessage = "A password must be at least {2} characters long.")]
    public string Password { get; set; } = string.Empty;
}
