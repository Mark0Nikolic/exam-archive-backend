using System.Text;
using System.Text.RegularExpressions;
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
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private readonly PaperFileStorage _storage;
    private readonly ILogger<PaperTextExtractor> _logger;

    public PaperTextExtractor(PaperFileStorage storage, ILogger<PaperTextExtractor> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    public string Extract(IEnumerable<PaperFile> files)
    {
        var pages = ExtractPages(files);
        if (pages.Count == 0)
        {
            return string.Empty;
        }

        // Running headers ("Jun 2025", the academy line) repeat on every page and
        // would otherwise be glued onto the question that crosses the page break.
        // Only the shared edges are removed, so a matrix row repeated inside one
        // question stays put.
        return string.Join(
            '\n',
            WithoutSharedBands(pages).Where(page => !string.IsNullOrWhiteSpace(page)));
    }

    internal static IReadOnlyList<string> WithoutSharedBands(IReadOnlyList<string> pages)
    {
        if (pages.Count < 2)
        {
            return pages;
        }

        var pageLines = pages.Select(SplitLines).ToArray();
        if (pageLines.Any(lines => lines.Count == 0))
        {
            return pages;
        }

        var prefix = SharedPrefix(pageLines);
        var suffix = SharedSuffix(pageLines, prefix);
        while (prefix + suffix > 0 && pageLines.Any(lines => lines.Count <= prefix + suffix))
        {
            if (suffix > 0)
            {
                suffix--;
            }
            else
            {
                prefix--;
            }
        }

        if (prefix == 0 && suffix == 0)
        {
            return pages;
        }

        return pageLines
            .Select(lines => string.Join('\n', lines.Skip(prefix).Take(lines.Count - prefix - suffix)))
            .ToArray();
    }

    private List<string> ExtractPages(IEnumerable<PaperFile> files)
    {
        var pages = new List<string>();

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

            if (type == PaperFileTypes.Pdf)
            {
                pages.AddRange(ExtractPdfPages(path));
            }
            else
            {
                var text = ExtractDocx(path);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    pages.Add(text);
                }
            }
        }

        return pages;
    }

    private static List<string> ExtractPdfPages(string path)
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

        return pages;
    }

    private static int SharedPrefix(List<string>[] pageLines)
    {
        var limit = pageLines.Min(lines => lines.Count);
        var count = 0;

        for (var i = 0; i < limit; i++)
        {
            if (QuestionSplitter.IsQuestionHeadingLine(pageLines[0][i]))
            {
                break;
            }

            var key = NormalizeLine(pageLines[0][i]);
            if (pageLines.Any(lines => NormalizeLine(lines[i]) != key))
            {
                break;
            }

            count++;
        }

        return count;
    }

    private static int SharedSuffix(List<string>[] pageLines, int prefix)
    {
        var limit = pageLines.Min(lines => lines.Count - prefix);
        var count = 0;

        for (var i = 1; i <= limit; i++)
        {
            var line = pageLines[0][^i];
            if (QuestionSplitter.IsQuestionHeadingLine(line))
            {
                break;
            }

            var key = NormalizeLine(line);
            if (pageLines.Any(lines => NormalizeLine(lines[^i]) != key))
            {
                break;
            }

            count++;
        }

        return count;
    }

    private static List<string> SplitLines(string page) =>
        page.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();

    private static string NormalizeLine(string line) =>
        Whitespace.Replace(line.Normalize(NormalizationForm.FormKC), " ").Trim().ToLowerInvariant();

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
