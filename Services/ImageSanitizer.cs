using ExamArchive.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace ExamArchive.Services;

// Removes the metadata a camera writes into a photograph before it is stored: EXIF
// carries GPS coordinates, capture time and device serial numbers, and approved
// papers are served publicly with no authentication. Stripping happens on upload so
// the data is never written to disk at all.
public sealed class ImageSanitizer
{
    // High on purpose: clearing metadata means re-encoding, and JPEG artefacts land
    // on the letter edges that have to stay readable.
    private const int JpegQuality = 92;

    // Guards against a decompression bomb — a few kilobytes can describe an image
    // whose decode allocates gigabytes.
    private const long MaxPixels = 80_000_000;

    private readonly ILogger<ImageSanitizer> _logger;

    public ImageSanitizer(ILogger<ImageSanitizer> logger)
    {
        _logger = logger;
    }

    // Documents carry metadata in a different structure and pass through untouched.
    public static bool CanSanitize(PaperFileType type) => PaperFileTypes.IsImage(type);

    // Returns the image with all metadata removed, or null if it could not be read
    // or is too large to decode safely. Buffered in memory because the caller needs
    // the length before writing.
    public async Task<MemoryStream?> SanitizeAsync(
        IFormFile file,
        PaperFileType type,
        CancellationToken cancellationToken)
    {
        await using var source = file.OpenReadStream();

        try
        {
            // Header only — reads dimensions without decoding pixels, which is what
            // makes the size check a defence rather than a formality.
            var info = await Image.IdentifyAsync(source, cancellationToken);

            if ((long)info.Width * info.Height > MaxPixels)
            {
                _logger.LogWarning(
                    "Refused an upload of {Width}x{Height}, which exceeds the {MaxPixels} pixel limit.",
                    info.Width, info.Height, MaxPixels);

                return null;
            }

            source.Position = 0;
            using var image = await Image.LoadAsync(source, cancellationToken);

            // Order matters: orientation is itself an EXIF tag, so the rotation is
            // baked into the pixels before the metadata is discarded.
            image.Mutate(context => context.AutoOrient());

            image.Metadata.ExifProfile = null;
            image.Metadata.IptcProfile = null;
            image.Metadata.XmpProfile = null;
            image.Metadata.IccProfile = null;

            var cleaned = new MemoryStream();
            await image.SaveAsync(cleaned, EncoderFor(type), cancellationToken);
            cleaned.Position = 0;

            return cleaned;
        }
        catch (Exception ex) when (ex is InvalidImageContentException or UnknownImageFormatException)
        {
            // The magic-byte check passed, so the file starts like an image but its
            // contents are malformed.
            _logger.LogWarning(ex, "Refused an upload that could not be decoded as an image.");
            return null;
        }
    }

    private static SixLabors.ImageSharp.Formats.ImageEncoder EncoderFor(PaperFileType type)
    {
        if (type == PaperFileTypes.Jpeg)
        {
            return new JpegEncoder { Quality = JpegQuality };
        }

        if (type == PaperFileTypes.Png)
        {
            return new PngEncoder();
        }

        if (type == PaperFileTypes.Webp)
        {
            return new WebpEncoder();
        }

        throw new ArgumentOutOfRangeException(
            nameof(type), type.ContentType, "No encoder is configured for this format.");
    }
}
