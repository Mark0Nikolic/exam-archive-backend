using System.Globalization;
using System.Text;

namespace ExamArchive.Data;

// Builds a small, valid, one-page PDF so seeded papers have real bytes behind them.
// Written by hand rather than pulled from a PDF library — the requirement is "a file
// a browser will open", and a production dependency for development placeholders
// would be a poor trade.
//
// The text is ASCII because the page uses Helvetica with WinAnsiEncoding, which has
// no Cyrillic, so pages are labelled with the subject code rather than the name.
internal static class SamplePdf
{
    private static readonly Encoding WinAnsi = Encoding.Latin1;

    public static byte[] Render(IReadOnlyList<string> lines)
    {
        var content = BuildContentStream(lines);

        // Object numbers are one-based and referenced by position, so this order is
        // load-bearing: the catalog is 1, and the content stream the page points at
        // is 5.
        string[] objects =
        [
            "<</Type/Catalog/Pages 2 0 R>>",
            "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]"
                + "/Resources<</Font<</F1 4 0 R>>>>/Contents 5 0 R>>",
            "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>",
            $"<</Length {WinAnsi.GetByteCount(content)}>>\nstream\n{content}\nendstream",
        ];

        var pdf = new MemoryStream();

        void Write(string text)
        {
            var bytes = WinAnsi.GetBytes(text);
            pdf.Write(bytes, 0, bytes.Length);
        }

        Write("%PDF-1.4\n");

        // Recorded as each object is written, because the cross-reference table below
        // is a list of byte offsets.
        var offsets = new long[objects.Length];

        for (var i = 0; i < objects.Length; i++)
        {
            offsets[i] = pdf.Length;
            Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var startXref = pdf.Length;

        Write($"xref\n0 {objects.Length + 1}\n");

        // Object zero is always the head of the free list, and the trailing space on
        // every entry is required: xref rows are exactly twenty bytes.
        Write("0000000000 65535 f \n");

        foreach (var offset in offsets)
        {
            Write(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        }

        Write($"trailer\n<</Size {objects.Length + 1}/Root 1 0 R>>\nstartxref\n{startXref}\n%%EOF\n");

        return pdf.ToArray();
    }

    private static string BuildContentStream(IReadOnlyList<string> lines)
    {
        var content = new StringBuilder("BT\n/F1 18 Tf\n72 760 Td\n");

        for (var i = 0; i < lines.Count; i++)
        {
            // Td is relative to the previous text position, so only the moves after
            // the first one carry a line height.
            if (i > 0)
            {
                content.Append("0 -28 Td\n");
            }

            content.Append('(').Append(Escape(lines[i])).Append(") Tj\n");
        }

        return content.Append("ET").ToString();
    }

    private static string Escape(string text) => text
        .Replace(@"\", @"\\")
        .Replace("(", @"\(")
        .Replace(")", @"\)");
}
