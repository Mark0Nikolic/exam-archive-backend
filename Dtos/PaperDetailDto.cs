using ExamArchive.Models;

namespace ExamArchive.Dtos;

/// <summary>
/// One paper, with its pages. What <see cref="PaperDto"/> carries, plus the
/// <see cref="Files"/> object that used to be a second request.
/// </summary>
/// <remarks>
/// The split between this and <see cref="PaperDto"/> is list-versus-detail rather
/// than public-versus-staff: a listing of twenty papers has no business carrying
/// every page of each, and a detail view would otherwise need a round trip to learn
/// what it is displaying.
/// </remarks>
/// <param name="Files">
/// The paper's pages, grouped by format. See <see cref="PaperFilesDto"/>.
/// </param>
public record PaperDetailDto(
    int Id,
    int SubjectId,
    string SubjectNameSr,
    string? SubjectNameEn,
    ExamType ExamType,
    int Month,
    int Year,
    int PageCount,
    DateTime UploadedAt,
    PaperStatus Status,
    DateTime? ReviewedAt,
    string? RejectionReason,
    PaperFilesDto Files);

/// <summary>
/// A paper's pages, grouped by format: one array per format present, keyed by the
/// format's canonical extension without the dot — <c>pdf</c>, <c>jpg</c>,
/// <c>png</c>, <c>webp</c>.
/// </summary>
/// <remarks>
/// Grouped rather than returned as one flat list because the formats are not
/// interchangeable to a client: a PDF is a document to hand to a PDF viewer, and a
/// set of JPEGs is a gallery. Sorting a flat list into those two piles is work every
/// client would otherwise repeat, and the server already knows the answer.
/// <para>
/// A format with no pages is absent rather than present-and-empty, so the keys say
/// what the paper actually contains. A client must therefore treat a missing key and
/// an empty array alike — a shape worth knowing about before writing against it.
/// </para>
/// <para>
/// The keys come from <see cref="PaperFileTypes"/> and change if a format is added
/// there, which is the same list the upload validation and the download content type
/// are built from.
/// </para>
/// </remarks>
public sealed class PaperFilesDto : Dictionary<string, List<PaperFileDto>>
{
    public PaperFilesDto(IEnumerable<PaperFileDto> files)

        // Ordinal, because these keys are ASCII format names rather than anything a
        // culture has an opinion about — and a culture-aware comparer is how "I" and
        // "i" stop matching under a Turkish locale.
        : base(StringComparer.Ordinal)
    {
        foreach (var group in files.GroupBy(FormatKey))
        {
            this[group.Key] = [.. group.OrderBy(f => f.PageNumber)];
        }
    }

    /// <summary>
    /// The grouping key for one page: its format's extension, less the dot.
    /// </summary>
    /// <remarks>
    /// The fallback cannot be reached through the upload endpoint, which refuses
    /// anything <see cref="PaperFileTypes.FromExtension"/> does not know and confirms
    /// the bytes as well, and the CK_PaperFile_ContentType check constraint refuses
    /// it at the database. It exists so that a row written around both — by a
    /// migration, or by hand — is grouped somewhere visible instead of throwing on
    /// read and taking the whole paper with it.
    /// </remarks>
    private static string FormatKey(PaperFileDto file) =>
        PaperFileTypes.FromContentType(file.ContentType)?.Extension.TrimStart('.')
            ?? "other";
}
