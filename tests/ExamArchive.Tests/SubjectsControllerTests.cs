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

public sealed class SubjectsControllerTests
{
    [Fact]
    public async Task YearPastTheStudyLengthReturnsBadRequest()
    {
        await using var db = await SeedCurriculumAsync();

        var action = await CreateController(db).GetSubjects(
            majorId: 1,
            yearOfStudy: 4,
            paging: new PageRequest { PerPage = 100 },
            cancellationToken: CancellationToken.None);

        var result = Assert.IsType<ObjectResult>(action.Result);
        var problem = Assert.IsType<ValidationProblemDetails>(result.Value);
        Assert.Contains(
            "outside the 3 years of major 1",
            Assert.Single(problem.Errors["yearOfStudy"]));
    }

    [Fact]
    public async Task YearInsideTheStudyLengthReturnsMatchingSubjects()
    {
        await using var db = await SeedCurriculumAsync();

        var action = await CreateController(db).GetSubjects(
            majorId: 1,
            yearOfStudy: 3,
            paging: new PageRequest { PerPage = 100 },
            cancellationToken: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<PagedResult<SubjectDto>>(ok.Value);
        var subject = Assert.Single(result.Data);
        Assert.Equal(1, subject.Id);
        Assert.Equal(3, subject.YearOfStudy);
    }

    [Fact]
    public async Task UnknownMajorWithAYearReturnsAnEmptyPage()
    {
        await using var db = await SeedCurriculumAsync();

        var action = await CreateController(db).GetSubjects(
            majorId: 999,
            yearOfStudy: 4,
            paging: new PageRequest { PerPage = 100 },
            cancellationToken: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<PagedResult<SubjectDto>>(ok.Value);
        Assert.Empty(result.Data);
        Assert.Equal(0, result.Meta.TotalItems);
    }

    [Fact]
    public async Task CreateSubjectAttachesItToTheMajor()
    {
        await using var db = await SeedCurriculumAsync();

        var action = await CreateController(db).CreateSubject(
            new CreateSubjectRequest
            {
                NameSr = "Базе података",
                NameEn = "Databases",
                Code = "IT240",
                MajorId = 1,
                YearOfStudy = 2
            },
            CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(action.Result);
        var dto = Assert.IsType<SubjectDto>(created.Value);
        Assert.Equal("IT240", dto.Code);
        Assert.Equal(2, dto.YearOfStudy);
        Assert.True(await db.MajorSubjects.AnyAsync(ms => ms.SubjectId == dto.Id && ms.MajorId == 1));
    }

    [Fact]
    public async Task DuplicateSubjectCodeReturnsConflict()
    {
        await using var db = await SeedCurriculumAsync();

        var action = await CreateController(db).CreateSubject(
            new CreateSubjectRequest
            {
                NameSr = "Други",
                Code = "TEST",
                MajorId = 1,
                YearOfStudy = 1
            },
            CancellationToken.None);

        var conflict = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task DeleteSubjectWithPapersReturnsConflict()
    {
        await using var db = await SeedCurriculumAsync();
        db.Papers.Add(new Paper
        {
            SubjectId = 1,
            ExamType = ExamType.Final,
            Month = 1,
            Year = 2024,
            Status = PaperStatus.Approved,
            UploadedAt = DateTime.UtcNow,
            ReviewedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var action = await CreateController(db).DeleteSubject(1, CancellationToken.None);

        var conflict = Assert.IsType<ObjectResult>(action);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task DeleteSubjectWithoutPapersReturnsNoContent()
    {
        await using var db = await SeedCurriculumAsync();

        var action = await CreateController(db).DeleteSubject(1, CancellationToken.None);

        Assert.IsType<NoContentResult>(action);
        Assert.False(await db.Subjects.AnyAsync(s => s.Id == 1));
        Assert.False(await db.MajorSubjects.AnyAsync(ms => ms.SubjectId == 1));
    }

    private static async Task<ExamArchiveDbContext> SeedCurriculumAsync()
    {
        var db = CreateDatabase();
        await db.Database.EnsureCreatedAsync();

        var subject = new Subject
        {
            Id = 1,
            Code = "TEST",
            NameSr = "Тест",
            NameEn = "Test"
        };

        db.Majors.Add(new Major
        {
            Id = 1,
            NameSr = "Рачунарске науке",
            NameEn = "Computer Science",
            StudiesId = 1
        });
        db.Subjects.Add(subject);
        db.MajorSubjects.Add(new MajorSubject
        {
            MajorId = 1,
            SubjectId = 1,
            YearOfStudy = 3
        });

        await db.SaveChangesAsync();
        return db;
    }

    private static ExamArchiveDbContext CreateDatabase()
    {
        var options = new DbContextOptionsBuilder<ExamArchiveDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new ExamArchiveDbContext(options);
    }

    private static SubjectsController CreateController(ExamArchiveDbContext db)
    {
        var user = new User { Id = 1, Username = "tester", Role = UserRole.User };

        return new SubjectsController(db, NullLogger<SubjectsController>.Instance)
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
