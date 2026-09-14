namespace ExamArchive.Models;

// One stored file belonging to a Paper — a whole scanned PDF, or a single
// photographed page. Moderation state stays on Paper; only the bytes live here.
public class PaperFile
{
    public int Id { get; set; }

    public int PaperId { get; set; }

    public Paper? Paper { get; set; }

    public string StoredPath { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    // Position within the paper, starting at 1, taken from the order the files arrived.
    public int PageNumber { get; set; }

    public long SizeBytes { get; set; }
}
