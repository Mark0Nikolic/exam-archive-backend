using System.ComponentModel.DataAnnotations;
using ExamArchive.Models;

namespace ExamArchive.Dtos;

public class CreateUserRequest
{
    [Required]
    [StringLength(50, MinimumLength = 3)]
    [RegularExpression(
        "^[a-zA-Z0-9._-]+$",
        ErrorMessage = "A username may contain only letters, digits, dots, dashes and underscores.")]
    public string Username { get; set; } = string.Empty;

    // Required does not reject a missing value on an enum — it binds to zero.
    [EnumDataType(typeof(UserRole), ErrorMessage = "A valid role is required.")]
    public UserRole Role { get; set; }
}
