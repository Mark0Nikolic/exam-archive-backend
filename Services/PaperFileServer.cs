using ExamArchive.Data;
using ExamArchive.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ExamArchive.Services;

// Turns a paper id and page number into a file response. One implementation rather
// than two, because the only difference between the public and moderation cases is
// whether unapproved papers are visible — and that is the security boundary.
public sealed class PaperFileServer
{
    private readonly ExamArchiveDbContext _db;
    private readonly PaperFileStorage _storage;
    private readonly ILogger<PaperFileServer> _logger;

    public PaperFileServer(
        ExamArchiveDbContext db,
        PaperFileStorage storage,
        ILogger<PaperFileServer> logger)
    {
        _db = db;
        _storage = storage;
        _logger = logger;
    }

    // approvedOnly is true for the public API, where a pending paper must be
    // indistinguishable from one that does not exist.
    public async Task<IActionResult> ServeAsync(
        HttpResponse response,
        int paperId,
        int pageNumber,
        bool approvedOnly,
        bool asAttachment,
        CancellationToken cancellationToken)
    {
        var page = await _db.PaperFiles
            .AsNoTracking()
            .Where(f => f.PaperId == paperId && f.PageNumber == pageNumber)
            .Where(f => !approvedOnly || f.Paper!.Status == PaperStatus.Approved)
            .Select(f => new
            {
                f.StoredPath,
                f.ContentType,
                SubjectCode = f.Paper!.Subject!.Code,
                SubjectName = f.Paper.Subject.NameSr,
                f.Paper.ExamType,
                f.Paper.Month,
                f.Paper.Year,
                PageCount = f.Paper.Files.Count
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (page is null)
        {
            return new NotFoundResult();
        }

        if (!_storage.TryResolve(page.StoredPath, out var absolutePath))
        {
            // Only reachable if a row's path was written by something other than the
            // upload endpoint, so treat it as corruption rather than a miss.
            _logger.LogError(
                "Paper {PaperId} page {PageNumber} has stored path {Path}, which escapes the uploads root.",
                paperId, pageNumber, page.StoredPath);

            return new NotFoundResult();
        }

        if (!File.Exists(absolutePath))
        {
            _logger.LogWarning(
                "Paper {PaperId} page {PageNumber} points at {Path}, which is missing from disk.",
                paperId, pageNumber, absolutePath);

            return new NotFoundResult();
        }

        var extension = PaperFileTypes.FromContentType(page.ContentType)?.Extension ?? string.Empty;

        PaperFileStorage.SetFileHeaders(
            response,
            PaperFileStorage.BuildDownloadName(
                page.SubjectCode, page.SubjectName, page.ExamType, page.Month, page.Year,
                pageNumber, page.PageCount, extension),
            asAttachment);

        // Range processing on: PDF viewers fetch the trailer first, then jump to the
        // pages they need.
        return new PhysicalFileResult(absolutePath, page.ContentType)
        {
            EnableRangeProcessing = true
        };
    }

    // Null when the paper is not visible to this caller.
    public async Task<List<Dtos.PaperFileDto>?> ListAsync(
        int paperId,
        bool approvedOnly,
        CancellationToken cancellationToken)
    {
        var visible = await _db.Papers
            .AsNoTracking()
            .AnyAsync(
                p => p.Id == paperId && (!approvedOnly || p.Status == PaperStatus.Approved),
                cancellationToken);

        if (!visible)
        {
            return null;
        }

        return await _db.PaperFiles
            .AsNoTracking()
            .Where(f => f.PaperId == paperId)
            .OrderBy(f => f.PageNumber)
            .Select(f => new Dtos.PaperFileDto(f.PageNumber, f.ContentType, f.SizeBytes))
            .ToListAsync(cancellationToken);
    }
}
