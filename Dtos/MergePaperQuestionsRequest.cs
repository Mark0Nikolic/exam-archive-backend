using System.ComponentModel.DataAnnotations;

namespace ExamArchive.Dtos;

public class MergePaperQuestionsRequest : IValidatableObject
{
    [Required]
    [MinLength(2, ErrorMessage = "Merge at least two questions.")]
    public List<int> Ordinals { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Ordinals.Any(ordinal => ordinal < 1))
        {
            yield return new ValidationResult(
                "Ordinals must be at least 1.",
                [nameof(Ordinals)]);
        }

        if (Ordinals.Count != Ordinals.Distinct().Count())
        {
            yield return new ValidationResult(
                "Ordinals must be unique.",
                [nameof(Ordinals)]);
        }
    }
}
