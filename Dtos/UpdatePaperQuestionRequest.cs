using System.ComponentModel.DataAnnotations;

namespace ExamArchive.Dtos;

public class UpdatePaperQuestionRequest : IValidatableObject
{
    [MaxLength(50, ErrorMessage = "A label cannot be longer than 50 characters.")]
    public string? Label { get; set; }

    public string? Text { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Label is null && Text is null)
        {
            yield return new ValidationResult(
                "Provide a label, text, or both.",
                [nameof(Label), nameof(Text)]);
        }

        if (Label is not null && string.IsNullOrWhiteSpace(Label))
        {
            yield return new ValidationResult(
                "A label cannot be empty.",
                [nameof(Label)]);
        }

        if (Text is not null && string.IsNullOrWhiteSpace(Text))
        {
            yield return new ValidationResult(
                "Question text cannot be empty.",
                [nameof(Text)]);
        }
    }
}
