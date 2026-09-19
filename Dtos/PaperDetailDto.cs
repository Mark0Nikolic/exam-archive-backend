using ExamArchive.Models;

namespace ExamArchive.Dtos;

public record PaperDetailDto(
    int Id,
    int SubjectId,
    string SubjectNameSr,
    string? SubjectNameEn,
    ExamType ExamType,
    int Month,
    int Year,
    int PageCount,
    DateTime UploadedAt,
    PaperStatus Status,
    DateTime? ReviewedAt,
    string? RejectionReason,
    PaperParseStatus ParseStatus,
    int QuestionCount,
    string? ParseError,
    PaperFilesDto Files);

// A paper's pages grouped by format, keyed by the format's extension without the dot.
// A format with no pages is absent rather than present-and-empty.
public sealed class PaperFilesDto : Dictionary<string, List<PaperFileDto>>
{
    public PaperFilesDto(IEnumerable<PaperFileDto> files)
        : base(StringComparer.Ordinal)
    {
        foreach (var group in files.GroupBy(FormatKey))
        {
            this[group.Key] = [.. group.OrderBy(f => f.PageNumber)];
        }
    }

    private static string FormatKey(PaperFileDto file) =>
        PaperFileTypes.FromContentType(file.ContentType)?.Extension.TrimStart('.')
            ?? "other";
}
