namespace ExamArchive.Dtos;

public sealed record PagedResult<T>(IReadOnlyList<T> Data, PageMeta Meta);
