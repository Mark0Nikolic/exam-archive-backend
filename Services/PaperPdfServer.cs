using ExamArchive.Data;
using ExamArchive.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ExamArchive.Services;

public sealed class PaperPdfServer
{
    private readonly ExamArchiveDbContext _db;
    private readonly PaperFileStorage _storage;
    private readonly PaperPdfComposer _composer;
    private readonly IPaperPdfCache _cache;
    private readonly ILogger<PaperPdfServer> _logger;

    public PaperPdfServer(
        ExamArchiveDbContext db,
        PaperFileStorage storage,
        PaperPdfComposer composer,
        IPaperPdfCache cache,
        ILogger<PaperPdfServer> logger)
    {
        _db = db;
        _storage = storage;
        _composer = composer;
        _cache = cache;
        _logger = logger;
    }

    public async Task<IActionResult> ServeAsync(
        HttpResponse response,
        int paperId,
        bool includeUnapproved,
        int? ownedByUserId,
        bool asAttachment,
        CancellationToken cancellationToken)
    {
        var paper = await _db.Papers
            .AsNoTracking()
            .Where(p => p.Id == paperId)
            .Where(p =>
                includeUnapproved
                || p.Status == PaperStatus.Approved
                || (ownedByUserId != null && p.SubmittedByUserId == ownedByUserId))
            .Select(p => new
            {
                p.Id,
                SubjectCode = p.Subject!.Code,
                SubjectName = p.Subject.NameSr,
                p.ExamType,
                p.Month,
                p.Year,
                Files = p.Files
                    .OrderBy(f => f.PageNumber)
                    .Select(f => new
                    {
                        f.Id,
                        f.PageNumber,
                        f.StoredPath,
                        f.ContentType,
                        f.SizeBytes
                    })
                    .ToList()
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (paper is null)
        {
            var exists = await _db.Papers
                .AsNoTracking()
                .AnyAsync(p => p.Id == paperId, cancellationToken);

            return exists ? AccessDenied() : new NotFoundResult();
        }

        if (paper.Files.Count == 0)
        {
            return new NotFoundResult();
        }

        var resolvedFiles = new List<ResolvedPaperFile>();
        foreach (var file in paper.Files)
        {
            if (!_storage.TryResolve(file.StoredPath, out var absolutePath)
                || !File.Exists(absolutePath))
            {
                _logger.LogWarning(
                    "Paper {PaperId} page {PageNumber} cannot be included because {Path} is missing or invalid.",
                    paperId,
                    file.PageNumber,
                    file.StoredPath);

                return new NotFoundResult();
            }

            resolvedFiles.Add(new ResolvedPaperFile(
                file.Id,
                file.PageNumber,
                file.StoredPath,
                absolutePath,
                file.ContentType,
                file.SizeBytes));
        }

        try
        {
            var contentVersion = PaperPdfCache.ContentVersion(resolvedFiles);
            var cached = await _cache.GetOrCreateAsync(
                paperId,
                contentVersion,
                (path, token) => _composer.ComposeAsync(path, resolvedFiles, token),
                cancellationToken);

            PaperFileStorage.SetFileHeaders(
                response,
                PaperFileStorage.BuildPaperDownloadName(
                    paper.SubjectCode,
                    paper.SubjectName,
                    paper.ExamType,
                    paper.Month,
                    paper.Year),
                asAttachment);

            return new PhysicalFileResult(cached.AbsolutePath, "application/pdf")
            {
                EnableRangeProcessing = true
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to compose paper {PaperId} as a PDF.", paperId);

            return new ObjectResult(new ProblemDetails
            {
                Title = "Paper preview unavailable",
                Detail = "The paper could not be converted to a combined PDF.",
                Status = StatusCodes.Status422UnprocessableEntity
            })
            {
                StatusCode = StatusCodes.Status422UnprocessableEntity
            };
        }
    }

    public void TryDeleteCache(int paperId) => _cache.TryDeletePaper(paperId);

    private static ObjectResult AccessDenied() =>
        new(new ProblemDetails
        {
            Title = "Paper access denied",
            Detail = "You may open approved papers and your own submissions only.",
            Status = StatusCodes.Status403Forbidden
        })
        {
            StatusCode = StatusCodes.Status403Forbidden
        };
}
