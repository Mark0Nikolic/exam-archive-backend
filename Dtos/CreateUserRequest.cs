using System.ComponentModel.DataAnnotations;
using ExamArchive.Models;

namespace ExamArchive.Dtos;

/// <summary>
/// An administrator creating a staff account.
/// </summary>
/// <remarks>
/// There is no password field. The server generates one, so an admin cannot set a
/// password they would then know — see <see cref="UserCredentialDto"/>.
/// </remarks>
public class CreateUserRequest
{
    /// <summary>The login name.</summary>
    /// <remarks>
    /// Restricted to a conservative set of characters because this string is
    /// compared case-insensitively, read aloud when handing over a password, and
    /// typed by someone who was told it verbally. Spaces and punctuation make all
    /// three worse, and lookalike Unicode would allow two accounts that no human
    /// can tell apart.
    /// </remarks>
    [Required]
    [StringLength(50, MinimumLength = 3)]
    [RegularExpression(
        "^[a-zA-Z0-9._-]+$",
        ErrorMessage = "A username may contain only letters, digits, dots, dashes and underscores.")]
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// What the account may do. Required rather than defaulted, so creating an
    /// administrator is always something someone typed on purpose.
    /// </summary>
    // Required does not reject a missing value on an enum — it binds to zero.
    // EnumDataType does, because zero is not a defined member of UserRole.
    [EnumDataType(typeof(UserRole), ErrorMessage = "A valid role is required.")]
    public UserRole Role { get; set; }
}
