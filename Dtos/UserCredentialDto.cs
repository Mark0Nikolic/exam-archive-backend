using ExamArchive.Models;

namespace ExamArchive.Dtos;

// The only response carrying a usable password, and it appears exactly once.
public record UserCredentialDto(
    int Id,
    string Username,
    UserRole Role,
    string TemporaryPassword);
