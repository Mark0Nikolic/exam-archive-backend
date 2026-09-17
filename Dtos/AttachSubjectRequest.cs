using System.ComponentModel.DataAnnotations;

namespace ExamArchive.Dtos;

public class AttachSubjectRequest
{
    [Range(1, int.MaxValue, ErrorMessage = "A subject id is required.")]
    public int SubjectId { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "YearOfStudy must be at least 1.")]
    public int YearOfStudy { get; set; }
}
