using System.ComponentModel.DataAnnotations;

namespace ExamArchive.Dtos;

public class RejectPaperRequest
{
    [Required(ErrorMessage = "A reason is required so the submitter knows what to fix.")]
    [StringLength(500, MinimumLength = 3, ErrorMessage = "The reason must be between 3 and 500 characters.")]
    public string Reason { get; set; } = string.Empty;
}
