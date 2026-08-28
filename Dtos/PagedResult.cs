namespace ExamArchive.Dtos;

/// <summary>
/// One page of a list: the rows, and where they sit in the whole result.
/// </summary>
/// <remarks>
/// An envelope rather than a bare array, because a page of results is not
/// self-describing: ten papers could be all of them or the first ten of four
/// hundred, and nothing in the array says which. Putting the count in a response
/// header instead would keep the array shape, at the cost of hiding the one
/// number every pager needs from anybody reading the body.
/// <para>
/// Every list endpoint returns this, including the ones short enough not to need
/// it. A client that can assume one shape for every listing writes the unwrapping
/// once; the alternative is remembering which four endpoints wrap and which two
/// do not, and finding out the hard way when one of them grows.
/// </para>
/// </remarks>
/// <param name="Data">The rows on this page, in the query's order.</param>
/// <param name="Meta">Which page this is, and how much there is to page through.</param>
public sealed record PagedResult<T>(IReadOnlyList<T> Data, PageMeta Meta);
