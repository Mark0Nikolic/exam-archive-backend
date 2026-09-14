namespace ExamArchive.Dtos;

public record PaperFileDto(
    int PageNumber,
    string ContentType,
    long SizeBytes);
