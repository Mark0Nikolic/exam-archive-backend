using ExamArchive.Models;

namespace ExamArchive.Dtos;

/// <summary>
/// An exam paper, in the one shape every paper endpoint returns.
/// </summary>
/// <remarks>
/// Status used to be absent here and present on a separate moderation DTO, because
/// the browse API only ever built this from approved rows and the field would have
/// implied a filter it did not offer. It offers that filter now — the listing takes
/// a status, and staff may ask for any of them — so the field describes something
/// real and the second DTO had nothing left to add.
/// <para>
/// Carrying the review fields costs no privacy. A caller who is not staff reaches
/// approved rows and only approved rows, where <paramref name="Status"/> is a value
/// they could already infer and <paramref name="RejectionReason"/> is always null:
/// approving a paper clears it. The one place a rejection reason is shown to the
/// person it was written for is <see cref="SubmissionStatusDto"/>, which is reached
/// by claim code or by owning the submission.
/// </para>
/// </remarks>
/// <param name="SubjectNameSr">
/// Carried on the row rather than left to a second request, because the browse
/// list can now span subjects and a paper that only knows its subject's id is
/// unrenderable without one lookup per row.
/// </param>
/// <param name="PageCount">
/// How many files make up the paper. A listing needs this to show "4 pages"
/// without fetching the pages themselves, and a client needs it to know the
/// range of page numbers it may request.
/// </param>
/// <param name="ReviewedAt">When the decision was made, or null while pending.</param>
/// <param name="RejectionReason">Why it was turned down. Null unless rejected.</param>
public record PaperDto(
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
    string? RejectionReason);
