namespace ExamArchive.Dtos;

public record PaperFileDto(
    int PageNumber,
    string ContentType,
    long SizeBytes,
    string Url)
{
    public static string PageUrl(int paperId, int pageNumber) =>
        $"/api/papers/{paperId}/pages/{pageNumber}";
}
