namespace ExamArchive.Dtos;

/// <summary>
/// Which slice of a list a client is asking for.
/// </summary>
/// <remarks>
/// Offset paging — a page number and a size — rather than a cursor. A cursor is
/// steadier under inserts and is the right choice for infinite scroll, but it
/// cannot answer "page 7 of 12", and numbered pages are what this archive's
/// listings are for. The cost is real and worth naming: a paper approved while
/// somebody is reading page 3 shifts everything after it down by one, so an item
/// can be seen twice or missed. For an archive that gains a few papers a week
/// that is a fair trade; for a feed it would not be.
/// <para>
/// Out-of-range values are clamped rather than rejected, and the response repeats
/// the values actually used. Rejecting would be defensible, but a 400 for
/// <c>pageSize=1000</c> teaches a client nothing it cannot learn from being handed
/// 100 and told so.
/// </para>
/// </remarks>
public sealed class PageRequest
{
    /// <summary>Rows per page when the client does not say.</summary>
    public const int DefaultPageSize = 20;

    /// <summary>
    /// The most rows one request can return.
    /// </summary>
    /// <remarks>
    /// The reason pagination is here at all: without a ceiling, a listing returns
    /// the whole table and gets slower every week the archive grows.
    /// </remarks>
    public const int MaxPageSize = 100;

    /// <summary>Which page, counting from one — the number a reader sees.</summary>
    public int Page { get; set; } = 1;

    /// <summary>How many rows to return, capped at <see cref="MaxPageSize"/>.</summary>
    public int PageSize { get; set; } = DefaultPageSize;
}
