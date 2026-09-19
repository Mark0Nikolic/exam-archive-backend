using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ExamArchive.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace ExamArchive.Services;

// Pulls selectable text out of PDF and Word pages. Image files and scanned PDFs
// with no text layer produce an empty result on purpose — there is no OCR here.
public sealed class PaperTextExtractor
{
    private readonly PaperFileStorage _storage;
    private readonly ILogger<PaperTextExtractor> _logger;

    public PaperTextExtractor(PaperFileStorage storage, ILogger<PaperTextExtractor> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    public string Extract(IEnumerable<PaperFile> files)
    {
        var parts = new List<string>();

        foreach (var file in files.OrderBy(f => f.PageNumber))
        {
            var type = PaperFileTypes.FromContentType(file.ContentType);
            if (type is null || !PaperFileTypes.IsDocument(type))
            {
                continue;
            }

            if (!_storage.TryResolve(file.StoredPath, out var path) || !File.Exists(path))
            {
                _logger.LogWarning(
                    "Skipping paper file {FileId} because {Path} is missing or invalid.",
                    file.Id,
                    file.StoredPath);
                continue;
            }

            var text = type == PaperFileTypes.Pdf
                ? ExtractPdf(path)
                : ExtractDocx(path);

            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text);
            }
        }

        return string.Join('\n', parts);
    }

    private static string ExtractPdf(string path)
    {
        using var document = PdfDocument.Open(path);
        var pages = new List<string>();

        foreach (var page in document.GetPages())
        {
            var text = ContentOrderTextExtractor.GetText(page);
            if (!string.IsNullOrWhiteSpace(text))
            {
                pages.Add(text);
            }
        }

        return string.Join('\n', pages);
    }

    private static string ExtractDocx(string path)
    {
        using var document = WordprocessingDocument.Open(path, isEditable: false);
        var body = document.MainDocumentPart?.Document.Body;
        if (body is null)
        {
            return string.Empty;
        }

        var paragraphs = body.Elements<Paragraph>()
            .Select(paragraph => paragraph.InnerText)
            .Where(text => text.Length > 0);

        return string.Join('\n', paragraphs);
    }
}
