using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace ExamArchive.Data;

internal static class SampleDocx
{
    public static byte[] Render(IReadOnlyList<string> paragraphs)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(
            stream, WordprocessingDocumentType.Document, autoSave: true))
        {
            var main = document.AddMainDocumentPart();
            var body = new Body();

            foreach (var paragraph in paragraphs)
            {
                body.AppendChild(new Paragraph(new Run(new Text(paragraph)
                {
                    Space = SpaceProcessingModeValues.Preserve
                })));
            }

            main.Document = new Document(body);
        }

        return stream.ToArray();
    }
}
