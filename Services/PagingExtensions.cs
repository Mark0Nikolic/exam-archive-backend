using ExamArchive.Dtos;
using Microsoft.EntityFrameworkCore;

namespace ExamArchive.Services;

/// <summary>
/// Turns a query into one page of results.
/// </summary>
/// <remarks>
/// Shared so that every listing clamps, counts and skips identically. Six
/// endpoints each doing their own arithmetic is six chances for one of them to be
/// off by a page.
/// </remarks>
public static class PagingExtensions
{
    /// <summary>
    /// Runs <paramref name="query"/> twice — once to count, once for the page.
    /// </summary>
    /// <remarks>
    /// The query must already carry a total ordering. A paged query whose order is
    /// ambiguous can return the same row on two pages and omit another entirely,
    /// because the database is free to break the tie differently on each call, and
    /// it will not look wrong on any single page.
    /// <para>
    /// Two round trips rather than a windowed COUNT(*) OVER(), which would fetch
    /// the total on every row. The count is cheap next to the projection, and this
    /// keeps the shape of the page query unchanged.
    /// </para>
    /// </remarks>
    public static async Task<PagedResult<T>> ToPagedResultAsync<T>(
        this IQueryable<T> query,
        PageRequest request,
        CancellationToken cancellationToken)
    {
        // Clamped here rather than at the edge, so the guarantee holds for every
        // caller: PageMeta is never handed a per-page of zero to divide by, and the
        // skip below is never negative.
        var perPage = Math.Clamp(request.PerPage, 1, PageRequest.MaxPerPage);
        var page = Math.Max(request.Page, 1);

        var totalItems = await query.CountAsync(cancellationToken);

        var meta = new PageMeta(page, perPage, totalItems);

        // Past the end is an empty page carrying honest totals, not a 404: a client
        // holding a stale page number after rows were deleted should be able to see
        // that it has overshot and step back, rather than handle an error.
        //
        // Skipping this branch also keeps the multiplication below in range, which
        // it would not be for a page number near int.MaxValue.
        var data = page > meta.TotalPages
            ? []
            : await query
                .Skip((page - 1) * perPage)
                .Take(perPage)
                .ToListAsync(cancellationToken);

        return new PagedResult<T>(data, meta);
    }
}
