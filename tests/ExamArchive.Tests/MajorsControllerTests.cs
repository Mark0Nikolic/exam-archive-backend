using ExamArchive.Controllers;
using ExamArchive.Data;
using ExamArchive.Dtos;
using ExamArchive.Models;
using ExamArchive.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExamArchive.Tests;

public sealed class MajorsControllerTests
{
    [Fact]
    public async Task CreateMajorUnderAStudy()
    {
        await using var db = await SeedAsync();

        var action = await CreateController(db).CreateMajor(
            new SaveMajorRequest
            {
                NameSr = "Телекомуникације",
                NameEn = "Telecommunications",
                StudiesId = 1
            },
            CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(action.Result);
        var dto = Assert.IsType<MajorDto>(created.Value);
        Assert.Equal(1, dto.StudiesId);
        Assert.Equal("Telecommunications", dto.NameEn);
    }

    [Fact]
    public async Task DeleteMajorHidingPapersReturnsConflict()
    {
        await using var db = await SeedAsync();
        db.Papers.Add(PaperOnSubject(1));
        await db.SaveChangesAsync();

        var action = await CreateController(db).DeleteMajor(1, CancellationToken.None);

        var conflict = Assert.IsType<ObjectResult>(action);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        Assert.True(await db.Majors.AnyAsync(m => m.Id == 1));
    }

    [Fact]
    public async Task DeleteMajorWhenPapersHaveAnotherMajorReturnsNoContent()
    {
        await using var db = await SeedAsync();
        db.Majors.Add(new Major
        {
            Id = 2,
            NameSr = "Други",
            NameEn = "Other",
            StudiesId = 1
        });
        db.MajorSubjects.Add(new MajorSubject { MajorId = 2, SubjectId = 1, YearOfStudy = 1 });
        db.Papers.Add(PaperOnSubject(1));
        await db.SaveChangesAsync();

        var action = await CreateController(db).DeleteMajor(1, CancellationToken.None);

        Assert.IsType<NoContentResult>(action);
        Assert.False(await db.Majors.AnyAsync(m => m.Id == 1));
        Assert.True(await db.Subjects.AnyAsync(s => s.Id == 1));
        Assert.True(await db.Papers.AnyAsync());
    }

    [Fact]
    public async Task AttachSubjectToAnotherMajor()
    {
        await using var db = await SeedAsync();
        db.Majors.Add(new Major
        {
            Id = 2,
            NameSr = "Други",
            NameEn = "Other",
            StudiesId = 1
        });
        await db.SaveChangesAsync();

        var action = await CreateController(db).AttachSubject(
            2,
            new AttachSubjectRequest { SubjectId = 1, YearOfStudy = 2 },
            CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(action.Result);
        var dto = Assert.IsType<SubjectDto>(created.Value);
        Assert.Equal(1, dto.Id);
        Assert.Equal(2, dto.YearOfStudy);
        Assert.True(await db.MajorSubjects.AnyAsync(ms => ms.MajorId == 2 && ms.SubjectId == 1));
    }

    [Fact]
    public async Task DetachSubjectFromOneMajorLeavesTheOtherLinkAndPapers()
    {
        await using var db = await SeedAsync();
        db.Majors.Add(new Major
        {
            Id = 2,
            NameSr = "Други",
            NameEn = "Other",
            StudiesId = 1
        });
        db.MajorSubjects.Add(new MajorSubject { MajorId = 2, SubjectId = 1, YearOfStudy = 2 });
        db.Papers.Add(PaperOnSubject(1));
        await db.SaveChangesAsync();

        var action = await CreateController(db).DetachSubject(1, 1, CancellationToken.None);

        Assert.IsType<NoContentResult>(action);
        Assert.False(await db.MajorSubjects.AnyAsync(ms => ms.MajorId == 1 && ms.SubjectId == 1));
        Assert.True(await db.MajorSubjects.AnyAsync(ms => ms.MajorId == 2 && ms.SubjectId == 1));
        Assert.True(await db.Subjects.AnyAsync(s => s.Id == 1));
        Assert.True(await db.Papers.AnyAsync(p => p.SubjectId == 1));
    }

    [Fact]
    public async Task DetachSubjectFromItsLastMajorLeavesTheSubjectAndPapers()
    {
        await using var db = await SeedAsync();
        db.Papers.Add(PaperOnSubject(1));
        await db.SaveChangesAsync();

        var action = await CreateController(db).DetachSubject(1, 1, CancellationToken.None);

        Assert.IsType<NoContentResult>(action);
        Assert.False(await db.MajorSubjects.AnyAsync(ms => ms.SubjectId == 1));
        Assert.True(await db.Subjects.AnyAsync(s => s.Id == 1));
        Assert.True(await db.Papers.AnyAsync(p => p.SubjectId == 1));

        var remaining = await new SubjectsController(db, NullLogger<SubjectsController>.Instance)
            .GetSubjects(
                majorId: 1,
                yearOfStudy: null,
                paging: new PageRequest { PerPage = 100 },
                cancellationToken: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(remaining.Result);
        var page = Assert.IsType<PagedResult<SubjectDto>>(ok.Value);
        Assert.Empty(page.Data);
    }

    [Fact]
    public async Task DetachMissingLinkReturnsNotFound()
    {
        await using var db = await SeedAsync();

        var action = await CreateController(db).DetachSubject(1, 999, CancellationToken.None);

        Assert.IsType<NotFoundResult>(action);
        Assert.True(await db.MajorSubjects.AnyAsync(ms => ms.MajorId == 1 && ms.SubjectId == 1));
    }

    private static async Task<ExamArchiveDbContext> SeedAsync()
    {
        var db = CreateDatabase();
        await db.Database.EnsureCreatedAsync();

        db.Majors.Add(new Major
        {
            Id = 1,
            NameSr = "Рачунарске науке",
            NameEn = "Computer Science",
            StudiesId = 1
        });
        db.Subjects.Add(new Subject { Id = 1, Code = "TEST", NameSr = "Тест", NameEn = "Test" });
        db.MajorSubjects.Add(new MajorSubject { MajorId = 1, SubjectId = 1, YearOfStudy = 3 });
        await db.SaveChangesAsync();
        return db;
    }

    private static Paper PaperOnSubject(int subjectId) =>
        new()
        {
            SubjectId = subjectId,
            ExamType = ExamType.Final,
            Month = 6,
            Year = 2024,
            Status = PaperStatus.Approved,
            UploadedAt = DateTime.UtcNow,
            ReviewedAt = DateTime.UtcNow
        };

    private static ExamArchiveDbContext CreateDatabase()
    {
        var options = new DbContextOptionsBuilder<ExamArchiveDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new ExamArchiveDbContext(options);
    }

    private static MajorsController CreateController(ExamArchiveDbContext db)
    {
        var user = new User { Id = 1, Username = "admin", Role = UserRole.Admin };

        return new MajorsController(db, NullLogger<MajorsController>.Instance)
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
}
