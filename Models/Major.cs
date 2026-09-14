namespace ExamArchive.Models;

public class Major
{
    public int Id { get; set; }

    public string NameSr { get; set; } = string.Empty;

    public string? NameEn { get; set; }

    public int StudiesId { get; set; }

    public Studies? Studies { get; set; }

    // Subjects are reached through the join entity, which carries YearOfStudy.
    public ICollection<MajorSubject> MajorSubjects { get; set; } = new List<MajorSubject>();
}
