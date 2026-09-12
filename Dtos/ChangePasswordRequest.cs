using System.ComponentModel.DataAnnotations;
using ExamArchive.Services;

namespace ExamArchive.Dtos;

public class ChangePasswordRequest
{
    [Required]
    [StringLength(128, MinimumLength = 1)]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required]
    [StringLength(
        128,
        MinimumLength = UserAccountService.MinimumPasswordLength,
        ErrorMessage = "The new password must be at least {2} characters.")]
    public string NewPassword { get; set; } = string.Empty;
}
