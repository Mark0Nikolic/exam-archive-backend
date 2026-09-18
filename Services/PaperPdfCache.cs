using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace ExamArchive.Services;

public sealed record CachedPaperPdf(string AbsolutePath, bool CacheHit, long SizeBytes);

public interface IPaperPdfCache
{
    Task<CachedPaperPdf> GetOrCreateAsync(
        int paperId,
        string contentVersion,
        Func<string, CancellationToken, Task> generateAsync,
        CancellationToken cancellationToken);

    void TryDeletePaper(int paperId);
}

public sealed class PaperPdfCache : IPaperPdfCache
{
    private readonly string _root;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _generationLocks = new();
    private readonly ConcurrentDictionary<int, byte> _deletedPapers = new();
    private readonly ILogger<PaperPdfCache> _logger;

    public PaperPdfCache(
        IWebHostEnvironment environment,
        IConfiguration configuration,
        ILogger<PaperPdfCache> logger)
    {
        var configured = configuration["Storage:GeneratedRoot"] ?? "generated";
        _root = Path.GetFullPath(
            Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(environment.ContentRootPath, configured));
        _logger = logger;

        CleanupAbandonedTemporaryFiles();
    }

    public async Task<CachedPaperPdf> GetOrCreateAsync(
        int paperId,
        string contentVersion,
        Func<string, CancellationToken, Task> generateAsync,
        CancellationToken cancellationToken)
    {
        if (_deletedPapers.ContainsKey(paperId))
        {
            throw new FileNotFoundException($"Paper {paperId} was deleted while its PDF was being generated.");
        }

        var finalPath = CachePath(paperId, contentVersion);
        if (TryGetValidFile(finalPath, out var cachedSize))
        {
            _logger.LogInformation(
                "Combined PDF cache hit for paper {PaperId}, version {ContentVersion}, {SizeBytes} bytes.",
                paperId,
                contentVersion,
                cachedSize);
            return new CachedPaperPdf(finalPath, CacheHit: true, cachedSize);
        }

        var cacheKey = $"{paperId}:{contentVersion}";
        var gate = _generationLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        var completed = false;

        try
        {
            if (TryGetValidFile(finalPath, out cachedSize))
            {
                completed = true;
                return new CachedPaperPdf(finalPath, CacheHit: true, cachedSize);
            }

            var directory = Path.GetDirectoryName(finalPath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(
                directory,
                $"{contentVersion}.{Guid.NewGuid():N}.tmp");
            var stopwatch = Stopwatch.StartNew();

            try
            {
                await generateAsync(temporaryPath, cancellationToken);
                if (_deletedPapers.ContainsKey(paperId))
                {
                    throw new FileNotFoundException($"Paper {paperId} was deleted while its PDF was being generated.");
                }
                var generated = new FileInfo(temporaryPath);
                if (!generated.Exists || generated.Length == 0)
                {
                    throw new InvalidOperationException("Combined PDF generation produced an empty file.");
                }

                try
                {
                    File.Move(temporaryPath, finalPath);
                }
                catch (IOException) when (File.Exists(finalPath))
                {
                    File.Delete(temporaryPath);
                }

                var size = new FileInfo(finalPath).Length;
                RemoveStaleVersions(directory, finalPath);
                _logger.LogInformation(
                    "Combined PDF cache miss for paper {PaperId}; generated version {ContentVersion} in {ElapsedMs} ms ({SizeBytes} bytes).",
                    paperId,
                    contentVersion,
                    stopwatch.ElapsedMilliseconds,
                    size);

                completed = true;
                return new CachedPaperPdf(finalPath, CacheHit: false, size);
            }
            catch
            {
                TryDelete(temporaryPath);
                throw;
            }
        }
        finally
        {
            gate.Release();
            if (completed)
            {
                _generationLocks.TryRemove(cacheKey, out _);
            }
        }
    }

    public void TryDeletePaper(int paperId)
    {
        _deletedPapers.TryAdd(paperId, 0);
        var directory = PaperDirectory(paperId);
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                exception,
                "Failed to delete the generated PDF cache for paper {PaperId}.",
                paperId);
        }
    }

    public static string ContentVersion(IEnumerable<ResolvedPaperFile> files)
    {
        var manifest = string.Join(
            "\n",
            files
                .OrderBy(file => file.PageNumber)
                .Select(file =>
                    $"{file.Id}|{file.PageNumber}|{file.StoredPath}|{file.SizeBytes}|{file.ContentType}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest)))
            .ToLowerInvariant();
    }

    private string CachePath(int paperId, string contentVersion)
    {
        if (paperId < 1 || contentVersion.Length != 64 || contentVersion.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new ArgumentOutOfRangeException(nameof(contentVersion), "Invalid generated PDF cache key.");
        }

        return Path.Combine(PaperDirectory(paperId), $"{contentVersion}.pdf");
    }

    private string PaperDirectory(int paperId) =>
        Path.Combine(_root, paperId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static bool TryGetValidFile(string path, out long size)
    {
        var file = new FileInfo(path);
        size = file.Exists ? file.Length : 0;
        if (size > 0)
        {
            return true;
        }

        if (file.Exists)
        {
            TryDelete(path);
        }
        return false;
    }

    private static void RemoveStaleVersions(string directory, string currentPath)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*.pdf"))
        {
            if (!path.Equals(currentPath, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(path);
            }
        }
    }

    private void CleanupAbandonedTemporaryFiles()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        var cutoff = DateTime.UtcNow.AddHours(-1);
        foreach (var path in Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                {
                    File.Delete(path);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(exception, "Could not remove abandoned generated PDF file {Path}.", path);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
