using ExamArchive.Data;
using ExamArchive.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SixLabors.ImageSharp;

namespace ExamArchive.Services;

public sealed class PaperPdfServer
{
    private const double A4WidthPoints = 595.28;
    private const double A4HeightPoints = 841.89;
    private const double PageMarginPoints = 18;

    private readonly ExamArchiveDbContext _db;
    private readonly PaperFileStorage _storage;
    private readonly ILogger<PaperPdfServer> _logger;

    public PaperPdfServer(
        ExamArchiveDbContext db,
        PaperFileStorage storage,
        ILogger<PaperPdfServer> logger)
    {
        _db = db;
        _storage = storage;
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
                    .Select(f => new { f.PageNumber, f.StoredPath, f.ContentType })
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

        var resolvedFiles = new List<(int PageNumber, string Path, string ContentType)>();
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

            resolvedFiles.Add((file.PageNumber, absolutePath, file.ContentType));
        }

        var temporaryPath = Path.Combine(
            Path.GetTempPath(),
            $"exam-archive-{paperId}-{Guid.NewGuid():N}.pdf");

        try
        {
            var outputStream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);

            try
            {
                using var output = new PdfDocument();
                var importedDocuments = new List<PdfDocument>();

                try
                {
                    foreach (var file in resolvedFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (file.ContentType == PaperFileTypes.Pdf.ContentType)
                        {
                            var imported = PdfReader.Open(file.Path, PdfDocumentOpenMode.Import);
                            importedDocuments.Add(imported);
                            for (var pageIndex = 0; pageIndex < imported.PageCount; pageIndex++)
                            {
                                output.AddPage(imported.Pages[pageIndex]);
                            }
                        }
                        else
                        {
                            AddImagePage(output, file.Path);
                        }
                    }

                    output.Save(outputStream, closeStream: false);
                }
                finally
                {
                    foreach (var imported in importedDocuments)
                    {
                        imported.Dispose();
                    }
                }

                outputStream.Position = 0;
                PaperFileStorage.SetFileHeaders(
                    response,
                    PaperFileStorage.BuildPaperDownloadName(
                        paper.SubjectCode,
                        paper.SubjectName,
                        paper.ExamType,
                        paper.Month,
                        paper.Year),
                    asAttachment);

                return new FileStreamResult(outputStream, "application/pdf")
                {
                    EnableRangeProcessing = true
                };
            }
            catch
            {
                await outputStream.DisposeAsync();
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to compose paper {PaperId} as a PDF.", paperId);
            TryDelete(temporaryPath);

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

    private static void AddImagePage(PdfDocument output, string path)
    {
        using var image = Image.Load(path);
        using var png = new MemoryStream();
        image.SaveAsPng(png);
        png.Position = 0;

        using var pdfImage = XImage.FromStream(png);
        var landscape = pdfImage.PixelWidth > pdfImage.PixelHeight;
        var pageWidth = landscape ? A4HeightPoints : A4WidthPoints;
        var pageHeight = landscape ? A4WidthPoints : A4HeightPoints;
        var page = output.AddPage();
        page.Width = XUnit.FromPoint(pageWidth);
        page.Height = XUnit.FromPoint(pageHeight);

        var imageWidth = pdfImage.PointWidth > 0 ? pdfImage.PointWidth : pdfImage.PixelWidth * 0.75;
        var imageHeight = pdfImage.PointHeight > 0 ? pdfImage.PointHeight : pdfImage.PixelHeight * 0.75;
        var scale = Math.Min(
            (pageWidth - 2 * PageMarginPoints) / imageWidth,
            (pageHeight - 2 * PageMarginPoints) / imageHeight);
        var width = imageWidth * scale;
        var height = imageHeight * scale;

        using var graphics = XGraphics.FromPdfPage(page);
        graphics.DrawImage(
            pdfImage,
            (pageWidth - width) / 2,
            (pageHeight - height) / 2,
            width,
            height);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

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
