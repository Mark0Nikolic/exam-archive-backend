namespace ExamArchive.Models;

// One use of a canonical question on a paper. Unique per (Paper, Question) so a
// file that reprints the same item from two sittings still stores it once.
public class PaperQuestion
{
    public int Id { get; set; }

    public int PaperId { get; set; }

    public Paper? Paper { get; set; }

    public int QuestionId { get; set; }

    public Question? Question { get; set; }

    // Position of the first occurrence in this paper, starting at 1.
    public int Ordinal { get; set; }

    // The heading as parsed, e.g. "1" or "Zadatak 2".
    public string Label { get; set; } = string.Empty;
}
