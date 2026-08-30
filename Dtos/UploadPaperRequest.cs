using System.ComponentModel.DataAnnotations;
using ExamArchive.Models;

namespace ExamArchive.Dtos;

/// <summary>
/// The multipart form a submitter fills in to add a paper to the archive.
/// A class rather than a record because <see cref="IFormFile"/> only binds from
/// form fields, which needs settable properties.
/// </summary>
public class UploadPaperRequest
{
    /// <summary>
    /// The paper's pages, in reading order — one PDF, or one image per page.
    /// </summary>
    /// <remarks>
    /// Order is taken from the order the parts arrive in the request, which is
    /// the order the client listed them. Multipart preserves this, so no separate
    /// page-number field is needed.
    /// </remarks>
    [Required(ErrorMessage = "At least one file is required.")]
    [MinLength(1, ErrorMessage = "At least one file is required.")]
    // Constant interpolation only folds string constants, so the count is spelled
    // out in the message and kept in step with MaxFiles by hand.
    [MaxLength(MaxFiles, ErrorMessage = "A paper cannot have more than 10 pages.")]
    public List<IFormFile> Files { get; set; } = [];

    /// <summary>
    /// Upper bound on pages in one submission. Enough for a real exam, low enough
    /// that a single request cannot tie up the server indefinitely.
    /// </summary>
    /// <remarks>
    /// The ceiling for the submission as a whole, whatever the files are. Formats may
    /// be mixed, and the only additional restriction is
    /// <see cref="Services.PaperSubmissionService.MaxPdfFiles"/>, which caps how many
    /// of them may be PDFs.
    /// </remarks>
    public const int MaxFiles = 10;

    /// <summary>The subject the paper belongs to. Must already exist.</summary>
    [Range(1, int.MaxValue, ErrorMessage = "SubjectId must be a positive id.")]
    public int SubjectId { get; set; }

    /// <summary>
    /// "Midterm", "Final" or "Resit". Matched case-insensitively.
    /// </summary>
    /// <remarks>
    /// Typed as the enum so model binding rejects anything else with a 400 before
    /// the action runs. As a string this had to be folded to the canonical casing
    /// by hand, and a value the constraint did not allow only surfaced as a
    /// database error at the end of the upload.
    /// </remarks>
    [Required(ErrorMessage = "ExamType is required.")]
    public ExamType ExamType { get; set; }

    /// <summary>Month the exam was held, 1-12.</summary>
    [Range(1, 12, ErrorMessage = "Month must be between 1 and 12.")]
    public int Month { get; set; }

    /// <summary>
    /// Year the exam was held. Bounds are checked in the controller rather than
    /// here — the upper bound is relative to today, which an attribute cannot express.
    /// </summary>
    public int Year { get; set; }
}
