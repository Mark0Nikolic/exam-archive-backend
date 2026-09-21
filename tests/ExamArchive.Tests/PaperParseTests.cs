using System.IO.Compression;
using System.Text;
using ExamArchive.Data;
using ExamArchive.Dtos;
using ExamArchive.Models;
using ExamArchive.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;

namespace ExamArchive.Tests;

public sealed class PaperParseTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "exam-archive-parse-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void DocxHeaderIsAcceptedAndRandomZipIsRejected()
    {
        using var docx = new MemoryStream(SampleDocx.Render(["1. A question"]));
        Assert.True(PaperFileTypes.Docx.Matches("PK"u8.ToArray()));
        Assert.True(PaperFileTypes.ContainsWordDocument(docx));

        using var zip = RandomZip();
        Assert.True(PaperFileTypes.Docx.Matches("PK"u8.ToArray()));
        Assert.False(PaperFileTypes.ContainsWordDocument(zip));
    }

    [Fact]
    public async Task StudentUploadDoesNotEnqueueParsing()
    {
        var (service, queue, db) = CreateSubmission();
        db.Subjects.Add(new Subject { Id = 1, Code = "IT240", NameSr = "Базе" });
        await db.SaveChangesAsync();

        var result = await service.SubmitAsync(
            Upload(SamplePdf.Render(["placeholder"])),
            PaperStatus.Pending,
            submittedByUserId: 10,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(PaperParseStatus.NotQueued, result.Paper!.ParseStatus);
        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public async Task StaffUploadEnqueuesParsing()
    {
        var (service, queue, db) = CreateSubmission();
        db.Subjects.Add(new Subject { Id = 1, Code = "IT240", NameSr = "Базе" });
        await db.SaveChangesAsync();

        var result = await service.SubmitAsync(
            Upload(SamplePdf.Render(["placeholder"])),
            PaperStatus.Approved,
            submittedByUserId: 11,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(PaperParseStatus.Queued, result.Paper!.ParseStatus);
        Assert.Equal([result.Paper.Id], queue.Enqueued);
    }

    [Fact]
    public async Task RandomZipWithADocxNameIsRejected()
    {
        var (service, _, db) = CreateSubmission();
        db.Subjects.Add(new Subject { Id = 1, Code = "IT240", NameSr = "Базе" });
        await db.SaveChangesAsync();

        using var zip = RandomZip();
        var result = await service.SubmitAsync(
            new UploadPaperRequest
            {
                SubjectId = 1,
                ExamType = ExamType.Final,
                Month = 6,
                Year = 2024,
                Files = [FormFile("paper.docx", PaperFileTypes.Docx.ContentType, zip.ToArray())]
            },
            PaperStatus.Pending,
            submittedByUserId: 10,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Message.Contains("DOCX"));
    }

    [Theory]
    [InlineData("scan.png")]
    [InlineData("scan.jpg")]
    [InlineData("scan.jpeg")]
    [InlineData("scan.webp")]
    public async Task ImageUploadsAreRejected(string fileName)
    {
        var (service, _, db) = CreateSubmission();
        db.Subjects.Add(new Subject { Id = 1, Code = "IT240", NameSr = "Базе" });
        await db.SaveChangesAsync();

        var result = await service.SubmitAsync(
            new UploadPaperRequest
            {
                SubjectId = 1,
                ExamType = ExamType.Final,
                Month = 6,
                Year = 2024,
                Files = [FormFile(fileName, "application/octet-stream", [1, 2, 3, 4])]
            },
            PaperStatus.Pending,
            submittedByUserId: 10,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Errors,
            error => error.Message.Contains("must be one of: .docx, .pdf."));
    }

    [Fact]
    public async Task ValidDocxIsAccepted()
    {
        var (service, _, db) = CreateSubmission();
        db.Subjects.Add(new Subject { Id = 1, Code = "IT240", NameSr = "Базе" });
        await db.SaveChangesAsync();

        var result = await service.SubmitAsync(
            new UploadPaperRequest
            {
                SubjectId = 1,
                ExamType = ExamType.Final,
                Month = 6,
                Year = 2024,
                Files = [FormFile(
                    "paper.docx",
                    PaperFileTypes.Docx.ContentType,
                    SampleDocx.Render(["1. What is SQL?"]))]
            },
            PaperStatus.Pending,
            submittedByUserId: 10,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(PaperFileTypes.Docx.ContentType, result.Paper!.Files[0].ContentType);
    }

    [Fact]
    public async Task ThreeDocumentsExceedTheCap()
    {
        var (service, _, db) = CreateSubmission();
        db.Subjects.Add(new Subject { Id = 1, Code = "IT240", NameSr = "Базе" });
        await db.SaveChangesAsync();

        var pdf = SamplePdf.Render(["page"]);
        var result = await service.SubmitAsync(
            new UploadPaperRequest
            {
                SubjectId = 1,
                ExamType = ExamType.Final,
                Month = 6,
                Year = 2024,
                Files =
                [
                    FormFile("a.pdf", PaperFileTypes.Pdf.ContentType, pdf),
                    FormFile("b.pdf", PaperFileTypes.Pdf.ContentType, pdf),
                    FormFile(
                        "c.docx",
                        PaperFileTypes.Docx.ContentType,
                        SampleDocx.Render(["1. Question"]))
                ]
            },
            PaperStatus.Pending,
            submittedByUserId: 10,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Message.Contains("at most 2"));
    }

    [Fact]
    public async Task ParsesWordQuestionsAndDeduplicatesWithinAPaper()
    {
        await using var db = CreateDatabase();
        var parser = CreateParser(db);
        var paper = await SeedPaperAsync(
            db,
            1,
            1,
            "questions.docx",
            PaperFileTypes.Docx.ContentType,
            SampleDocx.Render(
            [
                "June and February",
                "1. What is a primary key?",
                "2. Define a foreign key.",
                "1. What is a primary key?"
            ]));

        await parser.ParseAsync(paper.Id, CancellationToken.None);

        var stored = await db.Papers.SingleAsync();
        Assert.Equal(PaperParseStatus.Parsed, stored.ParseStatus);
        Assert.Equal(2, await db.Questions.CountAsync());
        Assert.Equal(2, await db.PaperQuestions.CountAsync());
        Assert.Equal(["1", "2"], await db.PaperQuestions
            .OrderBy(q => q.Ordinal)
            .Select(q => q.Label)
            .ToListAsync());
    }

    [Fact]
    public async Task ReusesAQuestionAcrossPapersOfTheSameSubject()
    {
        await using var db = CreateDatabase();
        var parser = CreateParser(db);
        var bytes = SampleDocx.Render(["1. What is a primary key?"]);
        var february = await SeedPaperAsync(
            db, 1, 1, "feb.docx", PaperFileTypes.Docx.ContentType, bytes);
        var june = await SeedPaperAsync(
            db, 2, 1, "jun.docx", PaperFileTypes.Docx.ContentType, bytes);

        await parser.ParseAsync(february.Id, CancellationToken.None);
        await parser.ParseAsync(june.Id, CancellationToken.None);

        Assert.Equal(1, await db.Questions.CountAsync());
        Assert.Equal(2, await db.PaperQuestions.CountAsync());
        Assert.Single((await db.PaperQuestions.ToListAsync()).Select(q => q.QuestionId).Distinct());
    }

    [Fact]
    public async Task SameTextInDifferentSubjectsIsNotDeduplicated()
    {
        await using var db = CreateDatabase();
        db.Subjects.Add(new Subject { Id = 2, Code = "IT230", NameSr = "Алгоритми" });
        await db.SaveChangesAsync();
        var parser = CreateParser(db);
        var bytes = SampleDocx.Render(["1. What is a primary key?"]);
        var first = await SeedPaperAsync(
            db, 1, 1, "a.docx", PaperFileTypes.Docx.ContentType, bytes);
        var second = await SeedPaperAsync(
            db, 2, 2, "b.docx", PaperFileTypes.Docx.ContentType, bytes);

        await parser.ParseAsync(first.Id, CancellationToken.None);
        await parser.ParseAsync(second.Id, CancellationToken.None);

        Assert.Equal(2, await db.Questions.CountAsync());
    }

    [Fact]
    public async Task ImageOnlyPaperIsSkipped()
    {
        await using var db = CreateDatabase();
        var parser = CreateParser(db);
        using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(16, 16);
        using var png = new MemoryStream();
        await image.SaveAsPngAsync(png);
        var paper = await SeedPaperAsync(
            db, 1, 1, "scan.png", PaperFileTypes.Png.ContentType, png.ToArray());

        await parser.ParseAsync(paper.Id, CancellationToken.None);

        var stored = await db.Papers.SingleAsync();
        Assert.Equal(PaperParseStatus.Skipped, stored.ParseStatus);
        Assert.Equal(PaperParseService.NoExtractableText, stored.ParseError);
        Assert.Empty(await db.Questions.ToListAsync());
    }

    [Fact]
    public async Task PdfWithoutNumberedQuestionsIsSkipped()
    {
        await using var db = CreateDatabase();
        var parser = CreateParser(db);
        var paper = await SeedPaperAsync(
            db,
            1,
            1,
            "notes.pdf",
            PaperFileTypes.Pdf.ContentType,
            SamplePdf.Render(["These are lecture notes, not an exam."]));

        await parser.ParseAsync(paper.Id, CancellationToken.None);

        var stored = await db.Papers.SingleAsync();
        Assert.Equal(PaperParseStatus.Skipped, stored.ParseStatus);
        Assert.Equal(PaperParseService.NoNumberedQuestions, stored.ParseError);
    }

    [Fact]
    public async Task RejectedPaperIsNotParsed()
    {
        await using var db = CreateDatabase();
        var parser = CreateParser(db);
        var paper = await SeedPaperAsync(
            db,
            1,
            1,
            "paper.docx",
            PaperFileTypes.Docx.ContentType,
            SampleDocx.Render(["1. What is SQL?"]),
            PaperStatus.Rejected);
        paper.ParseStatus = PaperParseStatus.Queued;
        await db.SaveChangesAsync();

        await parser.ParseAsync(paper.Id, CancellationToken.None);

        var stored = await db.Papers.SingleAsync();
        Assert.Equal(PaperParseStatus.NotQueued, stored.ParseStatus);
        Assert.Empty(await db.Questions.ToListAsync());
    }

    [Fact]
    public async Task ParsesNumberedQuestionsFromAPdf()
    {
        await using var db = CreateDatabase();
        var parser = CreateParser(db);
        var paper = await SeedPaperAsync(
            db,
            1,
            1,
            "exam.pdf",
            PaperFileTypes.Pdf.ContentType,
            SamplePdf.Render(
            [
                "1. What is a primary key?",
                "2. Define a foreign key."
            ]));

        await parser.ParseAsync(paper.Id, CancellationToken.None);

        var stored = await db.Papers.SingleAsync();
        Assert.Equal(PaperParseStatus.Parsed, stored.ParseStatus);
        Assert.Equal(2, await db.Questions.CountAsync());
    }

    [Fact]
    public async Task ReparseReplacesPreviousQuestionsAndDropsOrphans()
    {
        await using var db = CreateDatabase();
        var parser = CreateParser(db);
        var paper = await SeedPaperAsync(
            db,
            1,
            1,
            "exam.pdf",
            PaperFileTypes.Pdf.ContentType,
            SamplePdf.Render(
            [
                "1. What is a primary key?",
                "2. Define a foreign key."
            ]));

        await parser.ParseAsync(paper.Id, CancellationToken.None);
        db.Questions.Add(new Question
        {
            SubjectId = 1,
            Text = "Leftover from a bad split",
            ContentHash = QuestionText.Hash("Leftover from a bad split"),
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var leftover = await db.Questions.SingleAsync(q => q.Text.StartsWith("Leftover"));
        db.PaperQuestions.Add(new PaperQuestion
        {
            PaperId = paper.Id,
            QuestionId = leftover.Id,
            Ordinal = 3,
            Label = "3"
        });
        paper.ParseStatus = PaperParseStatus.Queued;
        await db.SaveChangesAsync();

        await parser.ParseAsync(paper.Id, CancellationToken.None);

        var stored = await db.Papers.SingleAsync();
        Assert.Equal(PaperParseStatus.Parsed, stored.ParseStatus);
        Assert.Equal(2, await db.PaperQuestions.CountAsync());
        Assert.Equal(2, await db.Questions.CountAsync());
        Assert.DoesNotContain(
            await db.Questions.Select(q => q.Text).ToListAsync(),
            text => text.StartsWith("Leftover"));
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

    private (PaperSubmissionService Service, RecordingPaperParseQueue Queue, ExamArchiveDbContext Db)
        CreateSubmission()
    {
        Directory.CreateDirectory(_root);
        var db = CreateDatabase();
        var queue = new RecordingPaperParseQueue();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:UploadsRoot"] = _root
            })
            .Build();
        var storage = new PaperFileStorage(new StubHostEnvironment(_root), configuration);
        var service = new PaperSubmissionService(
            db,
            storage,
            new ImageSanitizer(NullLogger<ImageSanitizer>.Instance),
            queue,
            NullLogger<PaperSubmissionService>.Instance);

        return (service, queue, db);
    }

    private PaperParseService CreateParser(ExamArchiveDbContext db)
    {
        Directory.CreateDirectory(_root);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:UploadsRoot"] = _root
            })
            .Build();
        var storage = new PaperFileStorage(new StubHostEnvironment(_root), configuration);

        return new PaperParseService(
            db,
            new PaperTextExtractor(storage, NullLogger<PaperTextExtractor>.Instance),
            new QuestionSplitter(),
            new PaperQuestionService(db, NullLogger<PaperQuestionService>.Instance),
            NullLogger<PaperParseService>.Instance);
    }

    private async Task<Paper> SeedPaperAsync(
        ExamArchiveDbContext db,
        int paperId,
        int subjectId,
        string fileName,
        string contentType,
        byte[] bytes,
        PaperStatus status = PaperStatus.Approved)
    {
        if (!await db.Subjects.AnyAsync(s => s.Id == subjectId))
        {
            db.Subjects.Add(new Subject
            {
                Id = subjectId,
                Code = "IT240",
                NameSr = "Базе"
            });
        }

        var relative = $"/uploads/2024/{fileName}";
        var absolute = Path.Combine(_root, "2024", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        await File.WriteAllBytesAsync(absolute, bytes);

        var paper = new Paper
        {
            Id = paperId,
            SubjectId = subjectId,
            ExamType = ExamType.Final,
            Month = 6,
            Year = 2024,
            Status = status,
            ReviewedAt = status == PaperStatus.Pending ? null : DateTime.UtcNow,
            ParseStatus = PaperParseStatus.Queued,
            UploadedAt = DateTime.UtcNow
        };

        paper.Files.Add(new PaperFile
        {
            StoredPath = relative,
            ContentType = contentType,
            PageNumber = 1,
            SizeBytes = bytes.Length
        });

        db.Papers.Add(paper);
        await db.SaveChangesAsync();
        return paper;
    }

    private static ExamArchiveDbContext CreateDatabase() =>
        new(new DbContextOptionsBuilder<ExamArchiveDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static UploadPaperRequest Upload(byte[] pdf) =>
        new()
        {
            SubjectId = 1,
            ExamType = ExamType.Final,
            Month = 6,
            Year = 2024,
            Files = [FormFile("paper.pdf", PaperFileTypes.Pdf.ContentType, pdf)]
        };

    private static FormFile FormFile(string fileName, string contentType, byte[] content)
    {
        var stream = new MemoryStream(content);
        return new FormFile(stream, 0, content.Length, "Files", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };
    }

    private static MemoryStream RandomZip()
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("readme.txt");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write("not a word document");
        }

        stream.Position = 0;
        return stream;
    }

    private sealed class RecordingPaperParseQueue : IPaperParseQueue
    {
        public List<int> Enqueued { get; } = [];

        public void Enqueue(int paperId) => Enqueued.Add(paperId);

        public async IAsyncEnumerable<int> ReadAllAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
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
