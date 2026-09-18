using ExamArchive.Models;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SixLabors.ImageSharp;

namespace ExamArchive.Services;

public sealed record ResolvedPaperFile(
    int Id,
    int PageNumber,
    string StoredPath,
    string AbsolutePath,
    string ContentType,
    long SizeBytes);

public sealed class PaperPdfComposer
{
    private const double A4WidthPoints = 595.28;
    private const double A4HeightPoints = 841.89;
    private const double PageMarginPoints = 18;

    public Task ComposeAsync(
        string destinationPath,
        IReadOnlyList<ResolvedPaperFile> files,
        CancellationToken cancellationToken)
    {
        using var output = new PdfDocument();
        var importedDocuments = new List<PdfDocument>();

        try
        {
            foreach (var file in files.OrderBy(file => file.PageNumber))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (file.ContentType == PaperFileTypes.Pdf.ContentType)
                {
                    var imported = PdfReader.Open(file.AbsolutePath, PdfDocumentOpenMode.Import);
                    importedDocuments.Add(imported);
                    for (var pageIndex = 0; pageIndex < imported.PageCount; pageIndex++)
                    {
                        output.AddPage(imported.Pages[pageIndex]);
                    }
                }
                else
                {
                    AddImagePage(output, file.AbsolutePath);
                }
            }

            output.Save(destinationPath);
            return Task.CompletedTask;
        }
        finally
        {
            foreach (var imported in importedDocuments)
            {
                imported.Dispose();
            }
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
}
