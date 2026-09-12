using ExamArchive.Models;

namespace ExamArchive.Dtos;

public record CurrentUserDto(int Id, string Username, UserRole Role);
