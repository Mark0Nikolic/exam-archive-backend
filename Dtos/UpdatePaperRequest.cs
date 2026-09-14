using System.ComponentModel.DataAnnotations;
using ExamArchive.Models;

namespace ExamArchive.Dtos;

public class UpdatePaperRequest
{
    // Range rather than Required: Required binds a missing int to zero.
    [Range(1, int.MaxValue, ErrorMessage = "A subject id is required.")]
    public int SubjectId { get; set; }

    public ExamType ExamType { get; set; }

    [Range(1, 12)]
    public int Month { get; set; }

    [Range(1970, 2100)]
    public int Year { get; set; }
}
