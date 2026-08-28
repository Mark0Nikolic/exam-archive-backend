namespace ExamArchive.Dtos;

/// <summary>
/// One page of a list, with what a client needs to render pagination around it.
/// </summary>
/// <remarks>
/// An envelope rather than a bare array, because a page of results is not
/// self-describing: twenty papers could be all of them or the first twenty of
/// four hundred, and nothing in the array says which. Putting the count in a
/// header instead would keep the array shape, at the cost of making the one
/// number every pager needs invisible to anybody reading the response body.
/// </remarks>
/// <param name="Page">The page actually returned, which is not always the one asked for.</param>
/// <param name="PageSize">The size actually used, after clamping.</param>
/// <param name="TotalCount">Rows matching the query across every page.</param>
public record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount)
{
    /// <summary>How many pages the full result divides into. Zero when nothing matched.</summary>
    public int TotalPages => TotalCount == 0
        ? 0
        : (int)Math.Ceiling(TotalCount / (double)PageSize);

    /// <summary>Whether a previous page exists — enough to enable or grey out a button.</summary>
    public bool HasPrevious => Page > 1;

    /// <summary>Whether a next page exists.</summary>
    public bool HasNext => Page < TotalPages;
}
