namespace ExamArchive.Models;

// A canonical exam question, unique per subject. The same wording appearing in
// February and June papers (or twice in one file) shares one row.
public class Question
{
    public int Id { get; set; }

    public int SubjectId { get; set; }

    public Subject? Subject { get; set; }

    public string Text { get; set; } = string.Empty;

    // SHA-256 hex of the normalized text. Uniqueness is (SubjectId, ContentHash).
    public string ContentHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public List<PaperQuestion> PaperQuestions { get; set; } = [];
}
