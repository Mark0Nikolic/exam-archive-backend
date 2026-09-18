using System.Reflection;
using ExamArchive.Controllers;
using ExamArchive.Data;
using ExamArchive.Dtos;
using ExamArchive.Models;
using ExamArchive.Services;
using Microsoft.AspNetCore.Authorization;
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

    [Fact]
    public async Task UnattachedListIncludesOrphansAndExcludesTaughtSubjects()
    {
        await using var db = await SeedCurriculumAsync();
        db.Subjects.Add(new Subject
        {
            Id = 2,
            Code = "DEAD",
            NameSr = "Укинути предмет",
            NameEn = "Dropped"
        });
        await db.SaveChangesAsync();

        var action = await CreateController(db, UserRole.Admin).GetUnattachedSubjects(
            new PageRequest { PerPage = 100 },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<PagedResult<SubjectDto>>(ok.Value);
        var subject = Assert.Single(result.Data);
        Assert.Equal(2, subject.Id);
        Assert.Equal(0, subject.YearOfStudy);
    }

    [Fact]
    public async Task LastDetachMovesTheSubjectOntoTheUnattachedList()
    {
        await using var db = await SeedCurriculumAsync();

        Assert.IsType<NoContentResult>(
            await new MajorsController(db, NullLogger<MajorsController>.Instance)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = UserAccountService.BuildPrincipal(
                            new User { Id = 1, Username = "admin", Role = UserRole.Admin })
                    }
                }
            }.DetachSubject(1, 1, CancellationToken.None));

        var action = await CreateController(db, UserRole.Admin).GetUnattachedSubjects(
            new PageRequest { PerPage = 100 },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<PagedResult<SubjectDto>>(ok.Value);
        var subject = Assert.Single(result.Data);
        Assert.Equal(1, subject.Id);
        Assert.Equal(0, subject.YearOfStudy);
    }

    [Fact]
    public async Task CatalogueListsAttachedAndUnattachedSubjectsWithPlacements()
    {
        await using var db = await SeedCurriculumAsync();
        db.Subjects.Add(new Subject
        {
            Id = 2,
            Code = "FREE",
            NameSr = "Слободан",
            NameEn = "Unattached"
        });
        await db.SaveChangesAsync();

        var action = await CreateController(db, UserRole.Admin).GetCatalogueSubjects(
            search: null,
            new PageRequest { PerPage = 100 },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<PagedResult<CatalogueSubjectDto>>(ok.Value);
        Assert.Equal(2, result.Data.Count);
        Assert.Empty(result.Data.Single(subject => subject.Id == 2).Placements);
        var placement = Assert.Single(result.Data.Single(subject => subject.Id == 1).Placements);
        Assert.Equal(1, placement.MajorId);
        Assert.Equal(1, placement.StudiesId);
        Assert.Equal(3, placement.YearOfStudy);
    }

    [Fact]
    public async Task CatalogueSearchMatchesCodeAndName()
    {
        await using var db = await SeedCurriculumAsync();

        var action = await CreateController(db, UserRole.Admin).GetCatalogueSubjects(
            search: "TEST",
            new PageRequest { PerPage = 100 },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<PagedResult<CatalogueSubjectDto>>(ok.Value);
        Assert.Equal(1, Assert.Single(result.Data).Id);
    }

    [Fact]
    public void CatalogueEndpointRequiresAdministratorPolicy()
    {
        var method = typeof(SubjectsController).GetMethod(
            nameof(SubjectsController.GetCatalogueSubjects),
            BindingFlags.Instance | BindingFlags.Public);

        var authorize = Assert.Single(method!.GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal(RolePolicies.Administrators, authorize.Policy);
    }

    [Fact]
    public async Task UpdateSubjectChangesIdentityAndOnePlacementYear()
    {
        await using var db = await SeedCurriculumAsync();

        var action = await CreateController(db, UserRole.Admin).UpdateSubject(
            1,
            new UpdateSubjectRequest
            {
                Code = "UPDATED",
                NameSr = "Измењен",
                NameEn = "Updated",
                MajorId = 1,
                YearOfStudy = 2
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var subject = Assert.IsType<SubjectDto>(ok.Value);
        Assert.Equal("UPDATED", subject.Code);
        Assert.Equal(2, subject.YearOfStudy);
        Assert.Equal(
            2,
            await db.MajorSubjects
                .Where(link => link.MajorId == 1 && link.SubjectId == 1)
                .Select(link => link.YearOfStudy)
                .SingleAsync());
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

    private static SubjectsController CreateController(
        ExamArchiveDbContext db,
        UserRole role = UserRole.User)
    {
        var user = new User { Id = 1, Username = "tester", Role = role };

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
