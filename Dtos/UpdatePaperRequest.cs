using System.ComponentModel.DataAnnotations;
using ExamArchive.Models;

namespace ExamArchive.Dtos;

public class UpdatePaperRequest : IValidatableObject
{
    // Range rather than Required: Required binds a missing int to zero.
    [Range(1, int.MaxValue, ErrorMessage = "A subject id is required.")]
    public int SubjectId { get; set; }

    public ExamType ExamType { get; set; }

    [Range(1, 12)]
    public int Month { get; set; }

    [Range(1990, int.MaxValue)]
    public int Year { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var maximumYear = DateTime.UtcNow.Year + 1;
        if (Year > maximumYear)
        {
            yield return new ValidationResult(
                $"Year must be between 1990 and {maximumYear}.",
                [nameof(Year)]);
        }
    }
}
