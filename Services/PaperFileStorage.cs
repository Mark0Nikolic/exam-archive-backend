using System.Globalization;
using System.Text;
using ExamArchive.Models;
using Microsoft.Net.Http.Headers;

namespace ExamArchive.Services;

// Owns the uploads folder: where a paper's pages are written, and how a stored path
// is turned back into a file that may safely be read. Both the browse and moderation
// controllers must apply the same containment rule, so it lives in one place.
public sealed class PaperFileStorage
{
    private const string UploadsPrefix = "uploads/";

    private readonly string _root;

    public PaperFileStorage(IWebHostEnvironment environment, IConfiguration configuration)
    {
        // Anchored to the content root: a relative path would resolve against the
        // process working directory, scattering uploads depending on how the app
        // was started.
        var configured = configuration["Storage:UploadsRoot"] ?? "uploads";
        _root = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured);
    }

    // The client's file name is never part of the stored path — it is
    // attacker-controlled and could carry separators or traversal segments.
    public static string BuildRelativePath(
        string? subjectCode,
        string subjectName,
        ExamType examType,
        int month,
        int year,
        string submissionId,
        int pageNumber,
        string extension)
    {
        var name = string.Create(
            CultureInfo.InvariantCulture,
            $"{SubjectSlug(subjectCode, subjectName)}-{examType.ToString().ToLowerInvariant()}-{year}-{month:D2}-{submissionId}-{pageNumber:D3}{extension}");

        return $"/uploads/{year}/{name}";
    }

    // The course code is preferred: it is plain ASCII, the same in every language,
    // and does not move when somebody fixes a typo in a subject name.
    private static string SubjectSlug(string? subjectCode, string subjectName) =>
        string.IsNullOrWhiteSpace(subjectCode) ? Slugify(subjectName) : Slugify(subjectCode);

    public static string NewSubmissionId() => Guid.NewGuid().ToString("N")[..8];

    // Refuses anything that resolves outside the uploads folder. Paths from
    // BuildRelativePath are always safe; rows edited by hand, seeded, or restored
    // from a backup may not be, and this is the one place a stored path becomes a
    // filesystem read.
    public bool TryResolve(string storedPath, out string absolutePath)
    {
        absolutePath = string.Empty;

        if (string.IsNullOrWhiteSpace(storedPath))
        {
            return false;
        }

        var relative = storedPath.Replace('\\', '/').TrimStart('/');

        if (relative.StartsWith(UploadsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            relative = relative[UploadsPrefix.Length..];
        }

        var root = Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(
            Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

        // GetFullPath has already collapsed any ".." segments, so comparing prefixes
        // is meaningful here in a way that inspecting the raw string would not be.
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        absolutePath = candidate;
        return true;
    }

    // For images, content is the sanitized stream rather than the uploaded one, so
    // stripped metadata never reaches the disk.
    public async Task<long> SaveAsync(
        Stream content,
        string relativePath,
        CancellationToken cancellationToken)
    {
        if (!TryResolve(relativePath, out var absolutePath))
        {
            throw new InvalidOperationException(
                $"Generated upload path '{relativePath}' resolved outside the uploads root.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);

        // CreateNew rather than Create: if the generated name somehow already exists,
        // fail loudly instead of overwriting somebody else's paper.
        await using var destination = new FileStream(
            absolutePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);

        await content.CopyToAsync(destination, cancellationToken);

        return destination.Length;
    }

    // Best effort: the upload has already failed, and masking that with a delete
    // error would only make it harder to diagnose.
    public void TryDeleteOrphans(IEnumerable<string> relativePaths, ILogger logger)
    {
        foreach (var relativePath in relativePaths)
        {
            if (!TryResolve(relativePath, out var absolutePath))
            {
                continue;
            }

            try
            {
                File.Delete(absolutePath);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to remove orphaned upload {Path} after the paper could not be saved.",
                    absolutePath);
            }
        }
    }

    // Rebuilt from the row rather than reusing the stored name, which carries a
    // uniqueness suffix that means nothing to whoever is saving the file.
    public static string BuildDownloadName(
        string? subjectCode,
        string subjectName,
        ExamType examType,
        int month,
        int year,
        int pageNumber,
        int pageCount,
        string extension)
    {
        var stem = string.Create(
            CultureInfo.InvariantCulture,
            $"{SubjectSlug(subjectCode, subjectName)}-{examType.ToString().ToLowerInvariant()}-{year}-{month:D2}");

        // Only genuine multi-page papers carry a page suffix.
        return pageCount > 1
            ? string.Create(CultureInfo.InvariantCulture, $"{stem}-p{pageNumber:D2}{extension}")
            : $"{stem}{extension}";
    }

    // nosniff is not optional here: these bytes came from a submitter and are served
    // inline, so without it a browser may decide the file looks like HTML and run it
    // as script from this origin.
    public static void SetFileHeaders(HttpResponse response, string downloadName, bool asAttachment)
    {
        response.Headers.XContentTypeOptions = "nosniff";

        var disposition = new ContentDispositionHeaderValue(asAttachment ? "attachment" : "inline");

        // Handles the quoting and the RFC 5987 encoded form for non-ASCII names.
        disposition.SetHttpFileName(downloadName);

        response.Headers.ContentDisposition = disposition.ToString();
    }

    // Cyrillic to Latin is the direction that works: њ is always "nj", whereas
    // reading "nj" back gives one letter in "коњ" and two in "инјекција". Three
    // letters expand to two characters, which is why this maps to strings.
    private static readonly Dictionary<char, string> CyrillicToLatin = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d",
        ['ђ'] = "dj", ['е'] = "e", ['ж'] = "z", ['з'] = "z", ['и'] = "i",
        ['ј'] = "j", ['к'] = "k", ['л'] = "l", ['љ'] = "lj", ['м'] = "m",
        ['н'] = "n", ['њ'] = "nj", ['о'] = "o", ['п'] = "p", ['р'] = "r",
        ['с'] = "s", ['т'] = "t", ['ћ'] = "c", ['у'] = "u", ['ф'] = "f",
        ['х'] = "h", ['ц'] = "c", ['ч'] = "c", ['џ'] = "dz", ['ш'] = "s"
    };

    private static readonly Dictionary<char, string> LatinDiacritics = new()
    {
        ['č'] = "c", ['ć'] = "c", ['đ'] = "dj", ['š'] = "s", ['ž'] = "z"
    };

    // Serbian text is transliterated rather than stripped: deleting non-ASCII
    // outright turned «Базе података» into an empty string and "Zaštita" into
    // "za-tita".
    private static string Slugify(string value)
    {
        var slug = new StringBuilder(value.Length);

        foreach (var c in value)
        {
            var lower = char.ToLowerInvariant(c);

            if (char.IsAsciiLetterOrDigit(lower))
            {
                slug.Append(lower);
            }
            else if (CyrillicToLatin.TryGetValue(lower, out var cyrillic))
            {
                slug.Append(cyrillic);
            }
            else if (LatinDiacritics.TryGetValue(lower, out var diacritic))
            {
                slug.Append(diacritic);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        return slug.ToString().TrimEnd('-') is { Length: > 0 } trimmed ? trimmed : "subject";
    }
}
