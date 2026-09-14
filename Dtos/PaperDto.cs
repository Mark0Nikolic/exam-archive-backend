using ExamArchive.Models;

namespace ExamArchive.Dtos;

public record PaperDto(
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
    bool IsOwnedByCurrentUser);
