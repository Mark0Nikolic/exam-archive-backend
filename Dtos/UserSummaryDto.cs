using ExamArchive.Models;

namespace ExamArchive.Dtos;

public record UserSummaryDto(
    int Id,
    string Username,
    UserRole Role,
    bool IsActive,
    bool MustChangePassword,
    DateTime CreatedAt);
