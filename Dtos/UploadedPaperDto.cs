using ExamArchive.Models;

namespace ExamArchive.Dtos;

public record UploadedPaperDto(
    int Id,
    int SubjectId,
    ExamType ExamType,
    int Month,
    int Year,
    DateTime UploadedAt,
    PaperStatus Status,
    IReadOnlyList<PaperFileDto> Files,
    string? ClaimToken)
{
    // claimToken is passed in rather than read from the paper, which only holds its hash.
    public static UploadedPaperDto From(Paper paper, string? claimToken = null) => new(
        paper.Id,
        paper.SubjectId,
        paper.ExamType,
        paper.Month,
        paper.Year,
        paper.UploadedAt,
        paper.Status,
        [.. paper.Files
            .OrderBy(f => f.PageNumber)
            .Select(f => new PaperFileDto(f.PageNumber, f.ContentType, f.SizeBytes))],
        claimToken);
}
