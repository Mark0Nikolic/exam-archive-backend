namespace ExamArchive.Dtos;

public sealed class PageRequest
{
    public const int DefaultPerPage = 10;

    public const int MaxPerPage = 100;

    public int Page { get; set; } = 1;

    public int PerPage { get; set; } = DefaultPerPage;
}
