namespace ExamArchive.Dtos;

/// <summary>
/// Where one page sits in the whole result: what a pager needs, and nothing about
/// the rows themselves.
/// </summary>
/// <remarks>
/// Split out from the rows rather than mixed in among them, because a client
/// reads the two at different moments — the rows to render a list, these four
/// numbers to draw the control under it — and because a key named <c>page</c>
/// sitting beside a paper's own fields would be ambiguous about which it
/// describes.
/// <para>
/// <see cref="Page"/> and <see cref="PerPage"/> report what was actually used
/// rather than what was asked for. A client that sends <c>perPage=1000</c> is
/// handed 100 and can see from the response that it was capped, which is a
/// gentler way to learn the ceiling than a rejected request.
/// </para>
/// </remarks>
/// <param name="Page">The page returned, counting from one.</param>
/// <param name="PerPage">Rows per page, after clamping.</param>
/// <param name="TotalItems">Rows matching the query across every page.</param>
public sealed record PageMeta(int Page, int PerPage, int TotalItems)
{
    /// <summary>
    /// How many pages the full result divides into. Zero when nothing matched.
    /// </summary>
    /// <remarks>
    /// Zero rather than one for an empty result, so that "is there anything here"
    /// and "how many pages" have the same answer, and a client rendering
    /// <c>1 of 0</c> has been told plainly that there is nothing to show.
    /// <para>
    /// Derived rather than stored: a page count that disagrees with the item count
    /// it was computed from is a bug that only shows up as an off-by-one at the end
    /// of a long list, and the only way to prevent it is to leave no way to set the
    /// two independently. <c>hasNext</c> is left out for the same reason — it is
    /// <c>Page &lt; TotalPages</c>, and a client can do that comparison as well as
    /// this record can.
    /// </para>
    /// </remarks>
    public int TotalPages => TotalItems == 0 || PerPage <= 0
        ? 0
        : (int)Math.Ceiling(TotalItems / (double)PerPage);
}
