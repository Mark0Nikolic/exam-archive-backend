namespace ExamArchive.Models;

public class PaperMetadataAudit
{
    public long Id { get; set; }

    public int PaperId { get; set; }

    public Paper? Paper { get; set; }

    public int? EditedByUserId { get; set; }

    public User? EditedByUser { get; set; }

    public DateTime EditedAt { get; set; }

    public int OldSubjectId { get; set; }

    public int NewSubjectId { get; set; }

    public ExamType OldExamType { get; set; }

    public ExamType NewExamType { get; set; }

    public int OldMonth { get; set; }

    public int NewMonth { get; set; }

    public int OldYear { get; set; }

    public int NewYear { get; set; }
}
