namespace ExamArchive.Dtos;

public sealed record PageMeta(int Page, int PerPage, int TotalItems)
{
    public int TotalPages => TotalItems == 0 || PerPage <= 0
        ? 0
        : (int)Math.Ceiling(TotalItems / (double)PerPage);
}
