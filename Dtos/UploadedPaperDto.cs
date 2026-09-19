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
    PaperParseStatus ParseStatus,
    int QuestionCount,
    IReadOnlyList<PaperFileDto> Files)
{
    public static UploadedPaperDto From(Paper paper) => new(
        paper.Id,
        paper.SubjectId,
        paper.ExamType,
        paper.Month,
        paper.Year,
        paper.UploadedAt,
        paper.Status,
        paper.ParseStatus,
        paper.PaperQuestions.Count,
        [.. paper.Files
            .OrderBy(f => f.PageNumber)
            .Select(f => new PaperFileDto(
                f.PageNumber,
                f.ContentType,
                f.SizeBytes,
                PaperFileDto.PageUrl(paper.Id, f.PageNumber)))]);
}
