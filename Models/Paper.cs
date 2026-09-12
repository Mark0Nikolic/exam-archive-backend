namespace ExamArchive.Models;

public class Paper
{
    public int Id { get; set; }

    public int SubjectId { get; set; }

    public Subject? Subject { get; set; }

    public List<PaperFile> Files { get; set; } = [];

    public ExamType ExamType { get; set; }

    public int Month { get; set; }

    public int Year { get; set; }

    // Left at default so the database fills it in on insert, in UTC.
    public DateTime UploadedAt { get; set; }

    // Nullable for papers predating accounts, and for those whose submitter was
    // deleted — deleting a user nulls this rather than cascading.
    public int? SubmittedByUserId { get; set; }

    public User? SubmittedBy { get; set; }

    // SHA-256 of the claim code shown once in the upload response. Only the hash is
    // kept, so a submitter who loses the code cannot be helped.
    public string? ClaimTokenHash { get; set; }

    public PaperStatus Status { get; set; } = PaperStatus.Pending;

    // Null on a decided paper means the decision predates this column.
    public DateTime? ReviewedAt { get; set; }

    // Cleared when a rejected paper is later approved, so it can never contradict
    // the status.
    public string? RejectionReason { get; set; }
}
