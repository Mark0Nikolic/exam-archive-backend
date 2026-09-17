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

public sealed class PapersControllerTests
{
    [Theory]
    [InlineData(UserRole.Moderator)]
    [InlineData(UserRole.Admin)]
    [InlineData(UserRole.SuperAdmin)]
    public async Task StaffRolesReceiveEveryPaperInTheRequiredOrder(UserRole role)
    {
        await using var db = CreateDatabase();
        await SeedAsync(
            db,
            Paper(1, PaperStatus.Pending, 11, 2020, 1, Utc(2024, 1, 1)),
            Paper(2, PaperStatus.Pending, 12, 2026, 6, Utc(2024, 1, 2)),
            Paper(3, PaperStatus.Rejected, 11, 2024, 6, Utc(2025, 1, 1)),
            Paper(4, PaperStatus.Rejected, 12, 2025, 1, Utc(2025, 1, 2)),
            Paper(5, PaperStatus.Approved, 11, 2023, 6, Utc(2025, 1, 3)),
            Paper(6, PaperStatus.Approved, 12, 2026, 1, Utc(2025, 1, 4)));

        var result = await GetAsync(CreateController(db, userId: 11, role));

        Assert.Equal([1, 2, 4, 3, 6, 5], result.Data.Select(p => p.Id));
        Assert.Equal(6, result.Meta.TotalItems);
        Assert.True(result.Data.Single(p => p.Id == 1).IsOwnedByCurrentUser);
        Assert.False(result.Data.Single(p => p.Id == 2).IsOwnedByCurrentUser);
    }

    [Fact]
    public async Task OrdinaryUserReceivesApprovedPapersAndOnlyTheirOwnUndecidedPapers()
    {
        await using var db = CreateDatabase();
        await SeedAsync(
            db,
            Paper(1, PaperStatus.Pending, 10, 2024, 1, Utc(2024, 1, 1)),
            Paper(2, PaperStatus.Pending, 20, 2024, 1, Utc(2024, 1, 2)),
            Paper(3, PaperStatus.Rejected, 10, 2024, 6, Utc(2024, 1, 3)),
            Paper(4, PaperStatus.Rejected, 20, 2025, 6, Utc(2024, 1, 4)),
            Paper(5, PaperStatus.Approved, 10, 2024, 6, Utc(2024, 1, 5)),
            Paper(6, PaperStatus.Approved, 20, 2025, 6, Utc(2024, 1, 6)));

        var result = await GetAsync(CreateController(db, userId: 10, UserRole.User));

        Assert.Equal([1, 3, 6, 5], result.Data.Select(p => p.Id));
        Assert.DoesNotContain(result.Data, p => p.Id is 2 or 4);
        Assert.Equal([true, true, false, true], result.Data.Select(p => p.IsOwnedByCurrentUser));
        Assert.Equal(4, result.Meta.TotalItems);
    }

    [Theory]
    [InlineData(PaperStatus.Pending, 1)]
    [InlineData(PaperStatus.Rejected, 3)]
    public async Task StatusFilterCannotBypassOrdinaryUserOwnership(
        PaperStatus status,
        int expectedPaperId)
    {
        await using var db = CreateDatabase();
        await SeedAsync(
            db,
            Paper(1, PaperStatus.Pending, 10, 2024, 1, Utc(2024, 1, 1)),
            Paper(2, PaperStatus.Pending, 20, 2024, 1, Utc(2024, 1, 2)),
            Paper(3, PaperStatus.Rejected, 10, 2024, 6, Utc(2024, 1, 3)),
            Paper(4, PaperStatus.Rejected, 20, 2024, 6, Utc(2024, 1, 4)));

        var result = await GetAsync(
            CreateController(db, userId: 10, UserRole.User),
            status: status);

        var paper = Assert.Single(result.Data);
        Assert.Equal(expectedPaperId, paper.Id);
        Assert.True(paper.IsOwnedByCurrentUser);
        Assert.Equal(1, result.Meta.TotalItems);
        Assert.Equal(1, result.Meta.TotalPages);
    }

    [Fact]
    public async Task TotalsAreCalculatedBeforeTheRequestedPageIsReturned()
    {
        await using var db = CreateDatabase();
        await SeedAsync(
            db,
            Paper(1, PaperStatus.Pending, 10, 2024, 1, Utc(2024, 1, 1)),
            Paper(2, PaperStatus.Pending, 20, 2024, 1, Utc(2024, 1, 2)),
            Paper(3, PaperStatus.Rejected, 10, 2024, 6, Utc(2024, 1, 3)),
            Paper(4, PaperStatus.Rejected, 20, 2025, 6, Utc(2024, 1, 4)),
            Paper(5, PaperStatus.Approved, 10, 2024, 6, Utc(2024, 1, 5)),
            Paper(6, PaperStatus.Approved, 20, 2025, 6, Utc(2024, 1, 6)));

        var result = await GetAsync(
            CreateController(db, userId: 99, UserRole.Moderator),
            page: 2,
            perPage: 2);

        Assert.Equal([4, 3], result.Data.Select(p => p.Id));
        Assert.Equal(2, result.Meta.Page);
        Assert.Equal(2, result.Meta.PerPage);
        Assert.Equal(6, result.Meta.TotalItems);
        Assert.Equal(3, result.Meta.TotalPages);
    }

    [Theory]
    [InlineData(PaperStatus.Pending)]
    [InlineData(PaperStatus.Rejected)]
    public async Task OrdinaryUserCanOpenTheirOwnUnapprovedPaper(PaperStatus status)
    {
        await using var db = CreateDatabase();
        await SeedAsync(db, Paper(1, status, 10, 2024, 1, Utc(2024, 1, 1)));

        var action = await CreateController(db, userId: 10, UserRole.User)
            .GetPaper(1, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var paper = Assert.IsType<PaperDetailDto>(ok.Value);
        Assert.Equal(1, paper.Id);
        Assert.Equal(status, paper.Status);
    }

    [Theory]
    [InlineData(PaperStatus.Pending)]
    [InlineData(PaperStatus.Rejected)]
    public async Task AnotherUsersUnapprovedPaperReturnsForbidden(PaperStatus status)
    {
        await using var db = CreateDatabase();
        await SeedAsync(db, Paper(1, status, 20, 2024, 1, Utc(2024, 1, 1)));

        var action = await CreateController(db, userId: 10, UserRole.User)
            .GetPaper(1, CancellationToken.None);

        var forbidden = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task UnknownPaperStillReturnsNotFound()
    {
        await using var db = CreateDatabase();
        await SeedAsync(db);

        var action = await CreateController(db, userId: 10, UserRole.User)
            .GetPaper(999, CancellationToken.None);

        Assert.IsType<NotFoundResult>(action.Result);
    }

    [Fact]
    public void BrowseAndSearchEndpointsRequireAuthentication()
    {
        Assert.NotNull(typeof(PapersController).GetCustomAttribute<AuthorizeAttribute>());
        Assert.NotNull(typeof(StudiesController).GetCustomAttribute<AuthorizeAttribute>());
        Assert.NotNull(typeof(MajorsController).GetCustomAttribute<AuthorizeAttribute>());
        Assert.NotNull(typeof(SubjectsController).GetCustomAttribute<AuthorizeAttribute>());

        Assert.Empty(Attributes<AllowAnonymousAttribute>(typeof(PapersController), "GetPapers"));
        Assert.Empty(Attributes<AllowAnonymousAttribute>(typeof(PapersController), "GetPaper"));

        Assert.NotEmpty(Attributes<AllowAnonymousAttribute>(typeof(AuthController), "Login"));
        Assert.NotEmpty(Attributes<AllowAnonymousAttribute>(typeof(AuthController), "Register"));

        Assert.Null(typeof(PapersController).GetMethod(
            "GetMySubmissions", BindingFlags.Instance | BindingFlags.Public));
        Assert.Null(typeof(PapersController).GetMethod(
            "GetSubmissionStatus", BindingFlags.Instance | BindingFlags.Public));
    }

    private static TAttribute[] Attributes<TAttribute>(Type controller, string methodName)
        where TAttribute : Attribute =>
        controller
            .GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public)!
            .GetCustomAttributes<TAttribute>()
            .ToArray();

    private static ExamArchiveDbContext CreateDatabase()
    {
        var options = new DbContextOptionsBuilder<ExamArchiveDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new ExamArchiveDbContext(options);
    }

    private static async Task SeedAsync(ExamArchiveDbContext db, params Paper[] papers)
    {
        var subject = new Subject
        {
            Id = 1,
            Code = "TEST",
            NameSr = "Тест",
            NameEn = "Test"
        };

        foreach (var paper in papers)
        {
            paper.SubjectId = subject.Id;
            paper.Subject = subject;
        }

        db.Subjects.Add(subject);
        db.Papers.AddRange(papers);
        await db.SaveChangesAsync();
    }

    private static Paper Paper(
        int id,
        PaperStatus status,
        int submittedByUserId,
        int year,
        int month,
        DateTime uploadedAt) =>
        new()
        {
            Id = id,
            ExamType = ExamType.Final,
            Month = month,
            Year = year,
            UploadedAt = uploadedAt,
            SubmittedByUserId = submittedByUserId,
            Status = status,
            ReviewedAt = status == PaperStatus.Pending ? null : uploadedAt.AddDays(1),
            RejectionReason = status == PaperStatus.Rejected ? "Rejected for testing." : null
        };

    private static DateTime Utc(int year, int month, int day) =>
        new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static PapersController CreateController(
        ExamArchiveDbContext db,
        int userId,
        UserRole role)
    {
        var user = new User { Id = userId, Username = $"user-{userId}", Role = role };
        var controller = new PapersController(
            db,
            storage: null!,
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

        return controller;
    }

    private static async Task<PagedResult<PaperDto>> GetAsync(
        PapersController controller,
        PaperStatus? status = null,
        int page = 1,
        int perPage = 100)
    {
        var action = await controller.GetPapers(
            studiesId: null,
            majorId: null,
            yearOfStudy: null,
            subjectId: null,
            examType: null,
            month: null,
            year: null,
            paging: new PageRequest { Page = page, PerPage = perPage },
            status: status,
            cancellationToken: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        return Assert.IsType<PagedResult<PaperDto>>(ok.Value);
    }
}
