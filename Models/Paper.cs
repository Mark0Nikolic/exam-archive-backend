namespace ExamArchive.Models;

/// <summary>
/// An uploaded exam paper belonging to a single <see cref="Subject"/>.
/// </summary>
public class Paper
{
    public int Id { get; set; }

    public int SubjectId { get; set; }

    public Subject? Subject { get; set; }

    /// <summary>
    /// The paper's pages, in reading order. A scan is a single PDF; a photographed
    /// paper is one image per page.
    /// </summary>
    public List<PaperFile> Files { get; set; } = [];

    /// <summary>Which sitting this paper is from. Stored as text and enforced by a check constraint.</summary>
    public ExamType ExamType { get; set; }

    /// <summary>Month the exam was held, 1-12.</summary>
    public int Month { get; set; }

    public int Year { get; set; }

    /// <summary>Left at default so the database fills it in on insert, in UTC.</summary>
    public DateTime UploadedAt { get; set; }

    /// <summary>
    /// The staff account that published this paper directly, or null — which is the
    /// normal case — for a paper that came in through the public upload.
    /// </summary>
    /// <remarks>
    /// Not a record of who submitted, because submitters are anonymous and no
    /// column could hold them. It records who used the staff upload endpoint, which
    /// is the one path that puts a paper into the archive without anybody reviewing
    /// it. That path deserves a name attached to it; the reviewed path already has
    /// a moderator's decision behind it.
    /// <para>
    /// Set to null rather than cascading when an account is removed, so deleting a
    /// user never deletes archived papers.
    /// </para>
    /// </remarks>
    public int? SubmittedByUserId { get; set; }

    public User? SubmittedBy { get; set; }

    /// <summary>
    /// SHA-256 of the claim code handed to the submitter, or null for papers that
    /// staff uploaded and for rows predating this column.
    /// </summary>
    /// <remarks>
    /// The submitter is anonymous, so there is no account to attach
    /// <see cref="RejectionReason"/> to and no address to send it to. This is the
    /// substitute: the code is shown once in the upload response, and presenting it
    /// again is the only way to learn what happened to the paper.
    /// <para>
    /// Only the hash is kept. The code itself exists in the response body and
    /// nowhere else — which is a real limitation and the honest trade for not
    /// identifying anybody: a submitter who loses it cannot be helped, because the
    /// archive genuinely does not know which paper was theirs.
    /// </para>
    /// </remarks>
    public string? ClaimTokenHash { get; set; }

    /// <summary>Moderation state. Stored as text and defaulted to Pending in the database.</summary>
    public PaperStatus Status { get; set; } = PaperStatus.Pending;

    /// <summary>
    /// When the paper left the pending queue, or null while it is still waiting.
    /// </summary>
    /// <remarks>
    /// Null on a decided paper means the decision predates this column, not that
    /// it was never made — the check constraint permits that rather than forcing a
    /// fabricated timestamp onto historical rows.
    /// </remarks>
    public DateTime? ReviewedAt { get; set; }

    /// <summary>
    /// Why the paper was turned down, shown to whoever submitted it. Null unless
    /// <see cref="Status"/> is <see cref="PaperStatus.Rejected"/>.
    /// </summary>
    /// <remarks>
    /// Cleared when a rejected paper is later approved, so the field can never
    /// contradict the status. That does discard the old reason: without a separate
    /// history table there is nowhere to keep it, and a stale reason sitting on an
    /// approved paper would be worse than none.
    /// </remarks>
    public string? RejectionReason { get; set; }
}
