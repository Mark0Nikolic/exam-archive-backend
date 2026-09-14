using ExamArchive.Data;
using ExamArchive.Dtos;
using ExamArchive.Models;
using Microsoft.EntityFrameworkCore;

namespace ExamArchive.Services;

public sealed record PaperSubmissionError(string Field, string Message);

public sealed class PaperSubmissionResult
{
    private PaperSubmissionResult(
        Paper? paper,
        string? claimToken,
        IReadOnlyList<PaperSubmissionError> errors)
    {
        Paper = paper;
        ClaimToken = claimToken;
        Errors = errors;
    }

    public Paper? Paper { get; }

    // Carried on the result rather than on the paper, which stores only the hash.
    // This is the single moment the code exists in readable form.
    public string? ClaimToken { get; }

    public IReadOnlyList<PaperSubmissionError> Errors { get; }

    public bool Succeeded => Paper is not null;

    public static PaperSubmissionResult Success(Paper paper, string? claimToken) =>
        new(paper, claimToken, []);

    public static PaperSubmissionResult Failed(IReadOnlyList<PaperSubmissionError> errors) =>
        new(null, null, errors);
}

// Validates a submitted paper, writes its pages to disk, and saves the row. Outside
// the controllers because two of them submit papers — the public endpoint queues,
// the staff endpoint publishes — and only the resulting status differs.
//
// Errors are returned rather than written to ModelState, which belongs to a
// controller.
public sealed class PaperSubmissionService
{
    public const long MaxFileSizeBytes = 20 * 1024 * 1024;

    public const long MaxTotalUploadBytes = 100 * 1024 * 1024;

    // A sub-limit within UploadPaperRequest.MaxFiles, which counts pages. A PDF is
    // already a whole document, so more than one means the paper arrived split.
    public const int MaxPdfFiles = 2;

    private const int MinYear = 1990;

    private readonly ExamArchiveDbContext _db;
    private readonly PaperFileStorage _storage;
    private readonly ImageSanitizer _sanitizer;
    private readonly ILogger<PaperSubmissionService> _logger;

    public PaperSubmissionService(
        ExamArchiveDbContext db,
        PaperFileStorage storage,
        ImageSanitizer sanitizer,
        ILogger<PaperSubmissionService> logger)
    {
        _db = db;
        _storage = storage;
        _sanitizer = sanitizer;
        _logger = logger;
    }

    // submittedByUserId is a parameter rather than a request field: it comes from
    // the session cookie the server issued, not from a claim the client can make.
    public async Task<PaperSubmissionResult> SubmitAsync(
        UploadPaperRequest request,
        PaperStatus initialStatus,
        int? submittedByUserId,
        CancellationToken cancellationToken)
    {
        var errors = new List<PaperSubmissionError>();

        // The subject's code and name are needed for the file names anyway, so this
        // doubles as the existence check — one query instead of two.
        var subject = await _db.Subjects
            .AsNoTracking()
            .Where(s => s.Id == request.SubjectId)
            .Select(s => new { s.Code, s.NameSr })
            .FirstOrDefaultAsync(cancellationToken);

        if (subject is null)
        {
            errors.Add(new PaperSubmissionError(
                nameof(request.SubjectId),
                $"Subject {request.SubjectId} does not exist."));
        }

        // Next year is allowed: an exam sat in January is often archived against the
        // academic year it belongs to rather than the calendar year it fell in.
        var maxYear = DateTime.UtcNow.Year + 1;
        if (request.Year < MinYear || request.Year > maxYear)
        {
            errors.Add(new PaperSubmissionError(
                nameof(request.Year),
                $"Year must be between {MinYear} and {maxYear}."));
        }

        // An empty list has already failed model validation, so skip the content
        // checks rather than report a second, confusing error on top of it.
        var fileTypes = request.Files.Count > 0
            ? await ValidateFilesAsync(request.Files, errors, cancellationToken)
            : null;

        if (errors.Count > 0 || fileTypes is null)
        {
            return PaperSubmissionResult.Failed(errors);
        }

        var submissionId = PaperFileStorage.NewSubmissionId();
        var written = new List<string>(request.Files.Count);

        // Issued only for submissions that go into the queue: a staff upload is
        // published immediately and its author can already see it.
        var claimToken = initialStatus == PaperStatus.Pending
            ? ClaimToken.Generate()
            : null;

        var paper = new Paper
        {
            SubjectId = request.SubjectId,
            ExamType = request.ExamType,
            Month = request.Month,
            Year = request.Year,
            Status = initialStatus,
            SubmittedByUserId = submittedByUserId,
            ClaimTokenHash = claimToken is null ? null : ClaimToken.Hash(claimToken),

            // A staff upload skips the queue, so the decision is made here and now.
            ReviewedAt = initialStatus == PaperStatus.Pending ? null : DateTime.UtcNow
        };

        try
        {
            // Files first, row second. The reverse order would leave a row pointing
            // at files that were never written; this order can at worst leave
            // unreferenced files, which the cleanup below handles.
            for (var i = 0; i < request.Files.Count; i++)
            {
                var pageNumber = i + 1;
                var type = fileTypes[i];

                var relativePath = PaperFileStorage.BuildRelativePath(
                    subject!.Code, subject.NameSr, request.ExamType,
                    request.Month, request.Year,
                    submissionId, pageNumber, type.Extension);

                // Images are cleaned of camera metadata before anything touches the
                // disk. A PDF has no EXIF block and passes through as uploaded.
                MemoryStream? sanitized = null;

                if (ImageSanitizer.CanSanitize(type))
                {
                    sanitized = await _sanitizer.SanitizeAsync(
                        request.Files[i], type, cancellationToken);

                    if (sanitized is null)
                    {
                        errors.Add(new PaperSubmissionError(
                            $"{nameof(UploadPaperRequest.Files)}[{i}]",
                            $"Page {pageNumber} could not be read as an image. It may be damaged."));

                        _storage.TryDeleteOrphans(written, _logger);
                        return PaperSubmissionResult.Failed(errors);
                    }
                }

                await using var content = sanitized
                    ?? (Stream)request.Files[i].OpenReadStream();

                var size = await _storage.SaveAsync(content, relativePath, cancellationToken);
                written.Add(relativePath);

                paper.Files.Add(new PaperFile
                {
                    StoredPath = relativePath,
                    ContentType = type.ContentType,
                    PageNumber = pageNumber,
                    SizeBytes = size
                });
            }

            _db.Papers.Add(paper);
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            _storage.TryDeleteOrphans(written, _logger);
            throw;
        }

        return PaperSubmissionResult.Success(paper, claimToken);
    }

    // Returns the resolved format of each file, positionally, or null if any failed.
    private static async Task<PaperFileType[]?> ValidateFilesAsync(
        List<IFormFile> files,
        List<PaperSubmissionError> errors,
        CancellationToken cancellationToken)
    {
        var resolved = new PaperFileType[files.Count];
        var totalBytes = 0L;
        var valid = true;

        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];

            // Page numbers, not indexes: the error is read by whoever picked the
            // files, and they counted from one.
            var label = $"{nameof(UploadPaperRequest.Files)}[{i}]";
            var page = i + 1;

            totalBytes += file.Length;

            if (file.Length == 0)
            {
                errors.Add(new PaperSubmissionError(label, $"Page {page} is empty."));
                valid = false;
                continue;
            }

            if (file.Length > MaxFileSizeBytes)
            {
                errors.Add(new PaperSubmissionError(
                    label,
                    $"Page {page} exceeds the {MaxFileSizeBytes / (1024 * 1024)} MB per-file limit."));
                valid = false;
                continue;
            }

            var type = PaperFileTypes.FromExtension(file.FileName);
            if (type is null)
            {
                errors.Add(new PaperSubmissionError(
                    label,
                    $"Page {page} must be one of: {string.Join(", ", PaperFileTypes.AcceptedExtensions)}."));
                valid = false;
                continue;
            }

            // An extension is just a claim by the client, so confirm the header too.
            await using var stream = file.OpenReadStream();
            var header = new byte[PaperFileTypes.MaxSignatureLength];
            var read = await stream.ReadAtLeastAsync(
                header, header.Length, throwOnEndOfStream: false, cancellationToken);

            if (!type.Matches(header.AsSpan(0, read)))
            {
                errors.Add(new PaperSubmissionError(
                    label,
                    $"Page {page} is not a valid {type.Extension[1..].ToUpperInvariant()} file."));
                valid = false;
                continue;
            }

            resolved[i] = type;
        }

        if (totalBytes > MaxTotalUploadBytes)
        {
            errors.Add(new PaperSubmissionError(
                nameof(UploadPaperRequest.Files),
                $"The submission exceeds the {MaxTotalUploadBytes / (1024 * 1024)} MB total limit."));
            valid = false;
        }

        // Only worth asking once every file has resolved to a format; until then
        // some entries of `resolved` are still null and the count would be wrong.
        // Formats may otherwise be mixed freely.
        if (valid)
        {
            var pdfCount = resolved.Count(t => t == PaperFileTypes.Pdf);

            if (pdfCount > MaxPdfFiles)
            {
                errors.Add(new PaperSubmissionError(
                    nameof(UploadPaperRequest.Files),
                    $"A submission may contain at most {MaxPdfFiles} PDFs, "
                        + $"and this one has {pdfCount}."));
                valid = false;
            }
        }

        return valid ? resolved : null;
    }
}
