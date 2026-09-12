using System.ComponentModel.DataAnnotations;
using ExamArchive.Services;

namespace ExamArchive.Dtos;

public class RegisterRequest
{
    [Required]
    [StringLength(50, MinimumLength = 3)]
    [RegularExpression(
        "^[a-zA-Z0-9._-]+$",
        ErrorMessage = "A username may contain only letters, digits, dots, dashes and underscores.")]
    public string Username { get; set; } = string.Empty;

    [Required]
    [StringLength(
        128,
        MinimumLength = UserAccountService.MinimumPasswordLength,
        ErrorMessage = "A password must be at least {2} characters long.")]
    public string Password { get; set; } = string.Empty;
}
