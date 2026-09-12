using ExamArchive.Dtos;
using Microsoft.EntityFrameworkCore;

namespace ExamArchive.Services;

public static class PagingExtensions
{
    // Runs the query twice — once to count, once for the page. The query must
    // already carry a total ordering, or a row can appear on two pages while
    // another is skipped.
    public static async Task<PagedResult<T>> ToPagedResultAsync<T>(
        this IQueryable<T> query,
        PageRequest request,
        CancellationToken cancellationToken)
    {
        // Clamped here rather than at the edge, so PageMeta is never handed a
        // per-page of zero and the skip below is never negative.
        var perPage = Math.Clamp(request.PerPage, 1, PageRequest.MaxPerPage);
        var page = Math.Max(request.Page, 1);

        var totalItems = await query.CountAsync(cancellationToken);

        var meta = new PageMeta(page, perPage, totalItems);

        // Past the end is an empty page with honest totals, not a 404. Skipping the
        // branch also keeps the multiplication below in range.
        var data = page > meta.TotalPages
            ? []
            : await query
                .Skip((page - 1) * perPage)
                .Take(perPage)
                .ToListAsync(cancellationToken);

        return new PagedResult<T>(data, meta);
    }
}
