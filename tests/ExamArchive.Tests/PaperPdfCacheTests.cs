using ExamArchive.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExamArchive.Tests;

public sealed class PaperPdfCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "exam-archive-pdf-cache-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SecondRequestUsesTheGeneratedFile()
    {
        var cache = CreateCache();
        var generations = 0;
        var version = new string('a', 64);

        var first = await cache.GetOrCreateAsync(
            1,
            version,
            (path, token) => GenerateAsync(path, token, () => Interlocked.Increment(ref generations)),
            CancellationToken.None);
        var second = await cache.GetOrCreateAsync(
            1,
            version,
            (path, token) => GenerateAsync(path, token, () => Interlocked.Increment(ref generations)),
            CancellationToken.None);

        Assert.False(first.CacheHit);
        Assert.True(second.CacheHit);
        Assert.Equal(first.AbsolutePath, second.AbsolutePath);
        Assert.Equal(1, generations);
    }

    [Fact]
    public async Task ConcurrentRequestsGenerateOnlyOnce()
    {
        var cache = CreateCache();
        var generations = 0;
        var version = new string('b', 64);

        var requests = Enumerable.Range(0, 8).Select(_ =>
            cache.GetOrCreateAsync(
                2,
                version,
                async (path, token) =>
                {
                    Interlocked.Increment(ref generations);
                    await Task.Delay(50, token);
                    await File.WriteAllBytesAsync(path, "%PDF-test"u8.ToArray(), token);
                },
                CancellationToken.None));

        var results = await Task.WhenAll(requests);

        Assert.Equal(1, generations);
        Assert.Single(results.Select(result => result.AbsolutePath).Distinct());
        Assert.Single(results, result => !result.CacheHit);
    }

    [Fact]
    public async Task FailedGenerationLeavesNoTemporaryFileAndCanRetry()
    {
        var cache = CreateCache();
        var version = new string('c', 64);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cache.GetOrCreateAsync(
                3,
                version,
                async (path, token) =>
                {
                    await File.WriteAllBytesAsync(path, "partial"u8.ToArray(), token);
                    throw new InvalidOperationException("generation failed");
                },
                CancellationToken.None));

        var result = await cache.GetOrCreateAsync(
            3,
            version,
            (path, token) => GenerateAsync(path, token),
            CancellationToken.None);

        Assert.True(File.Exists(result.AbsolutePath));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DeletingAPaperRemovesItsGeneratedDirectory()
    {
        var cache = CreateCache();
        var result = await cache.GetOrCreateAsync(
            4,
            new string('d', 64),
            (path, token) => GenerateAsync(path, token),
            CancellationToken.None);
        var directory = Path.GetDirectoryName(result.AbsolutePath)!;

        cache.TryDeletePaper(4);

        Assert.False(Directory.Exists(directory));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private PaperPdfCache CreateCache()
    {
        Directory.CreateDirectory(_root);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:GeneratedRoot"] = _root
            })
            .Build();

        return new PaperPdfCache(
            new StubHostEnvironment(_root),
            configuration,
            NullLogger<PaperPdfCache>.Instance);
    }

    private static async Task GenerateAsync(
        string path,
        CancellationToken cancellationToken,
        Action? beforeWrite = null)
    {
        beforeWrite?.Invoke();
        await File.WriteAllBytesAsync(path, "%PDF-test"u8.ToArray(), cancellationToken);
    }

    private sealed class StubHostEnvironment : IWebHostEnvironment
    {
        public StubHostEnvironment(string root) => ContentRootPath = root;

        public string ApplicationName { get; set; } = "ExamArchive.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; }
        public string EnvironmentName { get; set; } = "Development";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
