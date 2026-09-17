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

public sealed class StudiesControllerTests
{
    [Fact]
    public async Task GetStudiesIncludesYearsOfStudy()
    {
        await using var db = CreateDatabase();
        await db.Database.EnsureCreatedAsync();

        var action = await CreateController(db)
            .GetStudies(new PageRequest { PerPage = 100 }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<PagedResult<StudiesDto>>(ok.Value);

        Assert.Equal(2, result.Data.Count);
        Assert.Equal(3, result.Data[0].YearsOfStudy);
        Assert.Equal(2, result.Data[1].YearsOfStudy);
        Assert.Equal("Bachelor's", result.Data[0].NameEn);
        Assert.Equal("Master's", result.Data[1].NameEn);
    }

    [Fact]
    public async Task CreateStudiesReturnsTheNewRow()
    {
        await using var db = CreateDatabase();
        await db.Database.EnsureCreatedAsync();

        var action = await CreateController(db).CreateStudies(
            new SaveStudiesRequest
            {
                NameSr = "Докторске студије",
                NameEn = "Doctoral",
                YearsOfStudy = 1
            },
            CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(action.Result);
        var dto = Assert.IsType<StudiesDto>(created.Value);
        Assert.Equal(1, dto.YearsOfStudy);
        Assert.Equal("Doctoral", dto.NameEn);
        Assert.Equal(3, await db.Studies.CountAsync());
    }

    [Fact]
    public async Task DeleteStudiesWithMajorsReturnsConflict()
    {
        await using var db = CreateDatabase();
        await db.Database.EnsureCreatedAsync();
        db.Majors.Add(new Major { NameSr = "Тест", StudiesId = 1 });
        await db.SaveChangesAsync();

        var action = await CreateController(db).DeleteStudies(1, CancellationToken.None);

        var conflict = Assert.IsType<ObjectResult>(action);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task DeleteStudiesWithoutMajorsReturnsNoContent()
    {
        await using var db = CreateDatabase();
        await db.Database.EnsureCreatedAsync();
        db.Studies.Add(new Studies { NameSr = "Празно", YearsOfStudy = 1 });
        await db.SaveChangesAsync();
        var id = await db.Studies.Where(s => s.NameSr == "Празно").Select(s => s.Id).SingleAsync();

        var action = await CreateController(db).DeleteStudies(id, CancellationToken.None);

        Assert.IsType<NoContentResult>(action);
        Assert.False(await db.Studies.AnyAsync(s => s.Id == id));
    }

    [Fact]
    public async Task ShrinkingYearsBelowAnOccupiedYearReturnsConflict()
    {
        await using var db = CreateDatabase();
        await db.Database.EnsureCreatedAsync();
        db.Majors.Add(new Major { Id = 1, NameSr = "Тест", StudiesId = 1 });
        db.Subjects.Add(new Subject { Id = 1, NameSr = "Тест" });
        db.MajorSubjects.Add(new MajorSubject { MajorId = 1, SubjectId = 1, YearOfStudy = 3 });
        await db.SaveChangesAsync();

        var action = await CreateController(db).UpdateStudies(
            1,
            new SaveStudiesRequest
            {
                NameSr = "Основне академске студије",
                NameEn = "Bachelor's",
                YearsOfStudy = 2
            },
            CancellationToken.None);

        var conflict = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
    }

    private static ExamArchiveDbContext CreateDatabase()
    {
        var options = new DbContextOptionsBuilder<ExamArchiveDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new ExamArchiveDbContext(options);
    }

    private static StudiesController CreateController(ExamArchiveDbContext db)
    {
        var user = new User { Id = 1, Username = "tester", Role = UserRole.User };

        return new StudiesController(db, NullLogger<StudiesController>.Instance)
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
