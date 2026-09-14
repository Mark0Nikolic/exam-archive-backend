namespace ExamArchive.Models;

// A file format the archive accepts, with the byte signature that proves an upload
// really is that format. The signature is segmented because not every format puts
// its marker at offset zero.
public sealed class PaperFileType
{
    public required string ContentType { get; init; }

    public required string Extension { get; init; }

    // Extensions a client may submit, all folded to Extension.
    public required string[] AcceptedExtensions { get; init; }

    public required (int Offset, byte[] Bytes)[] Signature { get; init; }

    public int SignatureLength => Signature.Max(s => s.Offset + s.Bytes.Length);

    public bool Matches(ReadOnlySpan<byte> header)
    {
        foreach (var (offset, bytes) in Signature)
        {
            if (header.Length < offset + bytes.Length)
            {
                return false;
            }

            if (!header.Slice(offset, bytes.Length).SequenceEqual(bytes))
            {
                return false;
            }
        }

        return true;
    }
}

// The single source of truth for accepted formats: upload validation, the content
// type sent on download, and the CK_PaperFile_ContentType check constraint. Adding
// a format means updating that constraint in a migration too.
public static class PaperFileTypes
{
    public static readonly PaperFileType Pdf = new()
    {
        ContentType = "application/pdf",
        Extension = ".pdf",
        AcceptedExtensions = [".pdf"],
        Signature = [(0, "%PDF-"u8.ToArray())]
    };

    public static readonly PaperFileType Jpeg = new()
    {
        ContentType = "image/jpeg",
        Extension = ".jpg",
        AcceptedExtensions = [".jpg", ".jpeg"],

        // Only three bytes are fixed: the first segment's marker byte varies.
        Signature = [(0, [0xFF, 0xD8, 0xFF])]
    };

    public static readonly PaperFileType Png = new()
    {
        ContentType = "image/png",
        Extension = ".png",
        AcceptedExtensions = [".png"],
        Signature = [(0, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])]
    };

    public static readonly PaperFileType Webp = new()
    {
        ContentType = "image/webp",
        Extension = ".webp",
        AcceptedExtensions = [".webp"],

        // Bytes 4-7 are the file length, which is why the marker is split.
        Signature = [(0, "RIFF"u8.ToArray()), (8, "WEBP"u8.ToArray())]
    };

    public static readonly PaperFileType[] All = [Pdf, Jpeg, Png, Webp];

    public static readonly int MaxSignatureLength = All.Max(t => t.SignatureLength);

    public static readonly string[] AcceptedExtensions =
        [.. All.SelectMany(t => t.AcceptedExtensions).Order(StringComparer.Ordinal)];

    // The claim still has to be confirmed against the bytes — see PaperFileType.Matches.
    public static PaperFileType? FromExtension(string? fileName)
    {
        var extension = Path.GetExtension(fileName);

        if (string.IsNullOrEmpty(extension))
        {
            return null;
        }

        return All.FirstOrDefault(
            t => t.AcceptedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase));
    }

    public static PaperFileType? FromContentType(string contentType) =>
        All.FirstOrDefault(
            t => t.ContentType.Equals(contentType, StringComparison.OrdinalIgnoreCase));
}
