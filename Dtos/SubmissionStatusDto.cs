using ExamArchive.Models;

namespace ExamArchive.Dtos;

public record SubmissionStatusDto(
    int Id,
    string SubjectName,
    ExamType ExamType,
    int Month,
    int Year,
    DateTime UploadedAt,
    PaperStatus Status,
    DateTime? ReviewedAt,
    string? RejectionReason);
