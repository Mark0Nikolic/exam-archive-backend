using ExamArchive.Controllers;
using ExamArchive.Data;
using ExamArchive.Dtos;
using ExamArchive.Models;
using ExamArchive.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ExamArchive.Tests;

public sealed class PaperPageTests : IDisposable
{
    private readonly string _uploads = Path.Combine(
        Path.GetTempPath(),
        "exam-archive-pages-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ApprovedPageIsServedInline()
    {
        await using var db = await SeedPaperAsync(PaperStatus.Approved, submittedByUserId: 10);
        var controller = CreateController(db, userId: 99, UserRole.User);

        var result = await controller.GetPage(1, 1, download: false, CancellationToken.None);

        var file = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.True(file.EnableRangeProcessing);
        Assert.True(file.EnableRangeProcessing);
        Assert.Contains("inline", controller.Response.Headers.ContentDisposition.ToString());
        Assert.Contains("test-final-2024-06.pdf", controller.Response.Headers.ContentDisposition.ToString());
    }

    [Fact]
    public async Task DownloadQuerySetsAttachmentDisposition()
    {
        await using var db = await SeedPaperAsync(PaperStatus.Approved, submittedByUserId: 10);
        var controller = CreateController(db, userId: 99, UserRole.User);

        var result = await controller.GetPage(1, 1, download: true, CancellationToken.None);

        Assert.IsType<PhysicalFileResult>(result);
        Assert.Contains("attachment", controller.Response.Headers.ContentDisposition.ToString());
    }

    [Fact]
    public async Task OwnerCanOpenAPendingPage()
    {
        await using var db = await SeedPaperAsync(PaperStatus.Pending, submittedByUserId: 10);
        var controller = CreateController(db, userId: 10, UserRole.User);

        var result = await controller.GetPage(1, 1, download: false, CancellationToken.None);

        Assert.IsType<PhysicalFileResult>(result);
    }

    [Theory]
    [InlineData(UserRole.Moderator)]
    [InlineData(UserRole.Admin)]
    public async Task StaffCanOpenAPendingPage(UserRole role)
    {
        await using var db = await SeedPaperAsync(PaperStatus.Pending, submittedByUserId: 10);
        var controller = CreateController(db, userId: 11, role);

        var result = await controller.GetPage(1, 1, download: false, CancellationToken.None);

        Assert.IsType<PhysicalFileResult>(result);
    }

    [Fact]
    public async Task AnotherUserCannotOpenAPendingPage()
    {
        await using var db = await SeedPaperAsync(PaperStatus.Pending, submittedByUserId: 10);
        var controller = CreateController(db, userId: 99, UserRole.User);

        var result = await controller.GetPage(1, 1, download: false, CancellationToken.None);

        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task MissingPageReturnsNotFound()
    {
        await using var db = await SeedPaperAsync(PaperStatus.Approved, submittedByUserId: 10);
        var controller = CreateController(db, userId: 99, UserRole.User);

        var result = await controller.GetPage(1, 2, download: false, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task UnknownPaperReturnsNotFound()
    {
        await using var db = await SeedPaperAsync(PaperStatus.Approved, submittedByUserId: 10);
        var controller = CreateController(db, userId: 99, UserRole.User);

        var result = await controller.GetPage(999, 1, download: false, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task PaperDetailIncludesAPageUrl()
    {
        await using var db = await SeedPaperAsync(PaperStatus.Approved, submittedByUserId: 10);
        var controller = CreateController(db, userId: 99, UserRole.User);

        var action = await controller.GetPaper(1, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var paper = Assert.IsType<PaperDetailDto>(ok.Value);
        var page = Assert.Single(paper.Files["pdf"]);
        Assert.Equal("/api/papers/1/pages/1", page.Url);
    }

    [Fact]
    public async Task CombinedPreviewReturnsAnInlinePdf()
    {
        await using var db = await SeedPaperAsync(PaperStatus.Approved, submittedByUserId: 10);
        var controller = CreateController(db, userId: 99, UserRole.User);

        var result = await controller.PreviewPaper(1, CancellationToken.None);

        var file = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.Contains("inline", controller.Response.Headers.ContentDisposition.ToString());
        await using var stream = File.OpenRead(file.FileName);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        copy.Position = 0;
        using var pdf = PdfReader.Open(copy, PdfDocumentOpenMode.Import);
        Assert.Single(pdf.Pages);
    }

    [Fact]
    public async Task CombinedDownloadUsesAnAttachmentDisposition()
    {
        await using var db = await SeedPaperAsync(PaperStatus.Approved, submittedByUserId: 10);
        var controller = CreateController(db, userId: 99, UserRole.User);

        var result = await controller.DownloadPaper(1, CancellationToken.None);

        var file = Assert.IsType<PhysicalFileResult>(result);
        Assert.Contains("attachment", controller.Response.Headers.ContentDisposition.ToString());
        Assert.Contains("test-final-2024-06.pdf", controller.Response.Headers.ContentDisposition.ToString());
        Assert.True(File.Exists(file.FileName));
    }

    [Fact]
    public async Task CombinedPreviewConvertsImagesAndPreservesFileOrder()
    {
        await using var db = await SeedPaperAsync(
            PaperStatus.Approved,
            submittedByUserId: 10,
            includeImage: true);
        var controller = CreateController(db, userId: 99, UserRole.User);

        var result = await controller.PreviewPaper(1, CancellationToken.None);

        var file = Assert.IsType<PhysicalFileResult>(result);
        await using var stream = File.OpenRead(file.FileName);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        copy.Position = 0;
        using var pdf = PdfReader.Open(copy, PdfDocumentOpenMode.Import);
        Assert.Equal(2, pdf.PageCount);
    }

    [Fact]
    public async Task CombinedPreviewUsesTheSameVisibilityRulesAsPaperDetails()
    {
        await using var db = await SeedPaperAsync(PaperStatus.Pending, submittedByUserId: 10);
        var controller = CreateController(db, userId: 99, UserRole.User);

        var result = await controller.PreviewPaper(1, CancellationToken.None);

        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task MetadataChangesReuseTheSameCachedPdf()
    {
        await using var db = await SeedPaperAsync(PaperStatus.Approved, submittedByUserId: 10);
        var controller = CreateController(db, userId: 11, UserRole.Admin);
        var first = Assert.IsType<PhysicalFileResult>(
            await controller.PreviewPaper(1, CancellationToken.None));

        await controller.UpdatePaper(
            1,
            new UpdatePaperRequest
            {
                SubjectId = 1,
                ExamType = ExamType.Midterm,
                Month = 2,
                Year = 2025
            },
            CancellationToken.None);
        var second = Assert.IsType<PhysicalFileResult>(
            await controller.PreviewPaper(1, CancellationToken.None));

        Assert.Equal(first.FileName, second.FileName);
        Assert.Contains("test-midterm-2025-02.pdf", controller.Response.Headers.ContentDisposition.ToString());
    }

    [Fact]
    public async Task DeletingPaperRemovesItsCachedPdf()
    {
        await using var db = await SeedPaperAsync(PaperStatus.Approved, submittedByUserId: 10);
        var controller = CreateController(db, userId: 11, UserRole.Admin);
        var preview = Assert.IsType<PhysicalFileResult>(
            await controller.PreviewPaper(1, CancellationToken.None));
        var cacheDirectory = Path.GetDirectoryName(preview.FileName)!;

        var result = await controller.DeletePaper(1, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.False(Directory.Exists(cacheDirectory));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_uploads))
            {
                Directory.Delete(_uploads, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private async Task<ExamArchiveDbContext> SeedPaperAsync(
        PaperStatus status,
        int submittedByUserId,
        bool includeImage = false)
    {
        Directory.CreateDirectory(_uploads);

        var storedPath = "/uploads/2024/test-final-2024-06-abcd1234-001.pdf";
        var absolutePath = Path.Combine(_uploads, "2024", "test-final-2024-06-abcd1234-001.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        using (var document = new PdfDocument())
        {
            document.AddPage();
            document.Save(absolutePath);
        }

        var imageStoredPath = "/uploads/2024/test-final-2024-06-abcd1234-002.png";
        if (includeImage)
        {
            var imagePath = Path.Combine(_uploads, "2024", "test-final-2024-06-abcd1234-002.png");
            using var image = new Image<Rgba32>(32, 48);
            await image.SaveAsPngAsync(imagePath);
        }

        var db = new ExamArchiveDbContext(
            new DbContextOptionsBuilder<ExamArchiveDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

        await db.Database.EnsureCreatedAsync();

        db.Subjects.Add(new Subject { Id = 1, Code = "TEST", NameSr = "Тест", NameEn = "Test" });
        db.Papers.Add(new Paper
        {
            Id = 1,
            SubjectId = 1,
            ExamType = ExamType.Final,
            Month = 6,
            Year = 2024,
            Status = status,
            SubmittedByUserId = submittedByUserId,
            UploadedAt = DateTime.UtcNow,
            ReviewedAt = status == PaperStatus.Pending ? null : DateTime.UtcNow
        });
        db.PaperFiles.Add(new PaperFile
        {
            PaperId = 1,
            StoredPath = storedPath,
            ContentType = "application/pdf",
            PageNumber = 1,
            SizeBytes = 14
        });
        if (includeImage)
        {
            db.PaperFiles.Add(new PaperFile
            {
                PaperId = 1,
                StoredPath = imageStoredPath,
                ContentType = "image/png",
                PageNumber = 2,
                SizeBytes = 100
            });
        }
        await db.SaveChangesAsync();

        return db;
    }

    private PapersController CreateController(ExamArchiveDbContext db, int userId, UserRole role)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:UploadsRoot"] = _uploads,
                ["Storage:GeneratedRoot"] = Path.Combine(_uploads, "generated")
            })
            .Build();

        var environment = new StubHostEnvironment(_uploads);
        var storage = new PaperFileStorage(environment, configuration);
        var files = new PaperFileServer(db, storage, NullLogger<PaperFileServer>.Instance);
        var cache = new PaperPdfCache(environment, configuration, NullLogger<PaperPdfCache>.Instance);
        var pdfs = new PaperPdfServer(
            db,
            storage,
            new PaperPdfComposer(),
            cache,
            NullLogger<PaperPdfServer>.Instance);
        var user = new User { Id = userId, Username = $"user-{userId}", Role = role };

        return new PapersController(
            db,
            storage,
            files,
            pdfs,
            submissions: null!,
            NullLogger<PapersController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = UserAccountService.BuildPrincipal(user)
                }
            }
        };
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
