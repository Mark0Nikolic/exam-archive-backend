using System.ComponentModel.DataAnnotations;

namespace ExamArchive.Dtos;

public class SaveStudiesRequest
{
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string NameSr { get; set; } = string.Empty;

    [StringLength(100)]
    public string? NameEn { get; set; }

    // Range rather than Required: Required binds a missing int to zero.
    [Range(1, int.MaxValue, ErrorMessage = "YearsOfStudy must be at least 1.")]
    public int YearsOfStudy { get; set; }
}
