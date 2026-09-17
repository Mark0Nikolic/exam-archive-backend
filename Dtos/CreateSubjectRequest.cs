using System.ComponentModel.DataAnnotations;

namespace ExamArchive.Dtos;

public class CreateSubjectRequest
{
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string NameSr { get; set; } = string.Empty;

    [StringLength(200)]
    public string? NameEn { get; set; }

    [StringLength(20)]
    public string? Code { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "A major id is required.")]
    public int MajorId { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "YearOfStudy must be at least 1.")]
    public int YearOfStudy { get; set; }
}
