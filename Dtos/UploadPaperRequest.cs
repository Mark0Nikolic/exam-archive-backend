using System.ComponentModel.DataAnnotations;
using ExamArchive.Models;

namespace ExamArchive.Dtos;

public class UploadPaperRequest
{
    [Required(ErrorMessage = "At least one file is required.")]
    [MinLength(1, ErrorMessage = "At least one file is required.")]
    // Constant interpolation only folds string constants, so the count is spelled
    // out in the message and kept in step with MaxFiles by hand.
    [MaxLength(MaxFiles, ErrorMessage = "A paper cannot have more than 10 pages.")]
    public List<IFormFile> Files { get; set; } = [];

    public const int MaxFiles = 10;

    [Range(1, int.MaxValue, ErrorMessage = "SubjectId must be a positive id.")]
    public int SubjectId { get; set; }

    [Required(ErrorMessage = "ExamType is required.")]
    public ExamType ExamType { get; set; }

    [Range(1, 12, ErrorMessage = "Month must be between 1 and 12.")]
    public int Month { get; set; }

    // Bounds are checked in the controller: the upper one is relative to today.
    public int Year { get; set; }
}
