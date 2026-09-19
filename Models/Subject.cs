namespace ExamArchive.Models;

// A course/subject. The same subject can be taught in several majors, potentially
// in a different year of study for each.
public class Subject
{
    public int Id { get; set; }

    // The university's course code, e.g. "IT230". Unique where present, nullable
    // because the archive can be populated before every code is known. It is the
    // only name-like field that is the same in every language, which is why stored
    // file paths are built from it.
    public string? Code { get; set; }

    public string NameSr { get; set; } = string.Empty;

    public string? NameEn { get; set; }

    // Majors are reached through the join entity, which carries YearOfStudy.
    public ICollection<MajorSubject> MajorSubjects { get; set; } = new List<MajorSubject>();

    public ICollection<Paper> Papers { get; set; } = new List<Paper>();

    public ICollection<Question> Questions { get; set; } = new List<Question>();
}
