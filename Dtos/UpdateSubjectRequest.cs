using System.ComponentModel.DataAnnotations;

namespace ExamArchive.Dtos;

public class UpdateSubjectRequest
{
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string NameSr { get; set; } = string.Empty;

    [StringLength(200)]
    public string? NameEn { get; set; }

    [StringLength(20)]
    public string? Code { get; set; }

    // Optional: retarget the year in a major this subject already belongs to.
    // YearOfStudy is required when this is sent, checked in the action.
    [Range(1, int.MaxValue, ErrorMessage = "MajorId must be a positive id.")]
    public int? MajorId { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "YearOfStudy must be at least 1.")]
    public int? YearOfStudy { get; set; }
}
