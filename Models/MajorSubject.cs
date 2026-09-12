namespace ExamArchive.Models;

// Join entity between Major and Subject. It is explicit rather than a skip
// navigation because it carries YearOfStudy. Composite key configured in
// OnModelCreating.
public class MajorSubject
{
    public int MajorId { get; set; }

    public Major? Major { get; set; }

    public int SubjectId { get; set; }

    public Subject? Subject { get; set; }

    public int YearOfStudy { get; set; }
}
