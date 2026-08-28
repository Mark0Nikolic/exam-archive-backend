using System.Globalization;
using System.Text;

namespace ExamArchive.Data;

/// <summary>
/// Builds a small, valid, one-page PDF so that seeded papers have real bytes
/// behind them rather than a row pointing at nothing.
/// </summary>
/// <remarks>
/// Written by hand rather than pulled from a PDF library, because the whole
/// requirement is "a file a browser will open" and that is roughly six hundred
/// bytes of well-understood syntax. A dependency carried into production to
/// generate development placeholders would be a poor trade.
/// <para>
/// The text is deliberately ASCII: the page uses Helvetica with WinAnsiEncoding,
/// which has no Cyrillic, so it is labelled with the subject <em>code</em> rather
/// than the name. That is the one identifying field that reads the same in every
/// language anyway.
/// </para>
/// </remarks>
internal static class SamplePdf
{
    /// <summary>The encoding WinAnsiEncoding actually is, for the bytes written out.</summary>
    private static readonly Encoding WinAnsi = Encoding.Latin1;

    /// <summary>Renders <paramref name="lines"/> onto a single A4 page.</summary>
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

        // Recorded as each object is written, because the cross-reference table
        // below is a list of byte offsets and there is no way to know them up front.
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

    /// <summary>Lays the lines out top-down from a fixed origin.</summary>
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

    /// <summary>
    /// Escapes the three characters that would otherwise end the string literal or
    /// the escape sequence itself.
    /// </summary>
    private static string Escape(string text) => text
        .Replace(@"\", @"\\")
        .Replace("(", @"\(")
        .Replace(")", @"\)");
}
