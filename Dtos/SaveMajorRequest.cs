using System.ComponentModel.DataAnnotations;

namespace ExamArchive.Dtos;

public class SaveMajorRequest
{
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string NameSr { get; set; } = string.Empty;

    [StringLength(200)]
    public string? NameEn { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "A studies id is required.")]
    public int StudiesId { get; set; }
}
