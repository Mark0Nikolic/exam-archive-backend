using ExamArchive.Controllers;
using ExamArchive.Data;
using ExamArchive.Dtos;
using ExamArchive.Models;
using ExamArchive.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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

        return new StudiesController(db)
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
