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
    public async Task UpdatingPaperStoresTheOldAndNewMetadataInTheAuditTrail()
    {
        await using var db = CreateDatabase();
        await SeedAsync(db, Paper(1, PaperStatus.Approved, 10, 2024, 6, Utc(2024, 1, 1)));
        db.Subjects.Add(new Subject { Id = 2, Code = "NEXT", NameSr = "Нови", NameEn = "New" });
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId: 11, UserRole.Moderator);

        var action = await controller.UpdatePaper(
            1,
            new UpdatePaperRequest
            {
                SubjectId = 2,
                ExamType = ExamType.Midterm,
                Month = 2,
                Year = 2025
            },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(action.Result);
        var audit = await db.PaperMetadataAudits.SingleAsync();
        Assert.Equal(11, audit.EditedByUserId);
        Assert.Equal(1, audit.OldSubjectId);
        Assert.Equal(2, audit.NewSubjectId);
        Assert.Equal(ExamType.Final, audit.OldExamType);
        Assert.Equal(ExamType.Midterm, audit.NewExamType);
        Assert.Equal(6, audit.OldMonth);
        Assert.Equal(2, audit.NewMonth);
        Assert.Equal(2024, audit.OldYear);
        Assert.Equal(2025, audit.NewYear);
        Assert.Equal(DateTimeKind.Utc, audit.EditedAt.Kind);
    }

    [Fact]
    public async Task ApprovingAPendingPaperEnqueuesParsing()
    {
        await using var db = CreateDatabase();
        await SeedAsync(db, Paper(1, PaperStatus.Pending, 10, 2024, 6, Utc(2024, 1, 1)));
        var queue = new RecordingPaperParseQueue();
        var controller = CreateController(db, userId: 11, UserRole.Moderator, parseQueue: queue);

        var action = await controller.ApprovePaper(1, CancellationToken.None);

        Assert.IsType<OkObjectResult>(action.Result);
        var paper = await db.Papers.SingleAsync();
        Assert.Equal(PaperStatus.Approved, paper.Status);
        Assert.Equal(PaperParseStatus.Queued, paper.ParseStatus);
        Assert.Equal([1], queue.Enqueued);
    }

    [Fact]
    public async Task ApprovingAnAlreadyApprovedPaperDoesNotEnqueueParsing()
    {
        await using var db = CreateDatabase();
        var existing = Paper(1, PaperStatus.Approved, 10, 2024, 6, Utc(2024, 1, 1));
        existing.ParseStatus = PaperParseStatus.Parsed;
        await SeedAsync(db, existing);
        var queue = new RecordingPaperParseQueue();
        var controller = CreateController(db, userId: 11, UserRole.Moderator, parseQueue: queue);

        var action = await controller.ApprovePaper(1, CancellationToken.None);

        Assert.IsType<OkObjectResult>(action.Result);
        Assert.Empty(queue.Enqueued);
        Assert.Equal(PaperParseStatus.Parsed, (await db.Papers.SingleAsync()).ParseStatus);
    }

    [Fact]
    public async Task RejectingAPaperDoesNotEnqueueParsing()
    {
        await using var db = CreateDatabase();
        await SeedAsync(db, Paper(1, PaperStatus.Pending, 10, 2024, 6, Utc(2024, 1, 1)));
        var queue = new RecordingPaperParseQueue();
        var controller = CreateController(db, userId: 11, UserRole.Moderator, parseQueue: queue);

        var action = await controller.RejectPaper(
            1,
            new RejectPaperRequest { Reason = "Unreadable scan." },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(action.Result);
        Assert.Empty(queue.Enqueued);
        Assert.Equal(PaperParseStatus.NotQueued, (await db.Papers.SingleAsync()).ParseStatus);
    }

    [Fact]
    public async Task StaffCanReadParsedQuestions()
    {
        await using var db = CreateDatabase();
        await SeedAsync(db, Paper(1, PaperStatus.Approved, 10, 2024, 6, Utc(2024, 1, 1)));
        db.Questions.Add(new Question
        {
            Id = 1,
            SubjectId = 1,
            Text = "What is a primary key?",
            ContentHash = QuestionText.Hash("What is a primary key?"),
            CreatedAt = DateTime.UtcNow
        });
        db.PaperQuestions.Add(new PaperQuestion
        {
            PaperId = 1,
            QuestionId = 1,
            Ordinal = 1,
            Label = "1"
        });
        await db.SaveChangesAsync();

        var action = await CreateController(db, userId: 11, UserRole.Moderator)
            .GetPaperQuestions(1, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var body = Assert.IsType<PaperQuestionsDto>(ok.Value);
        var question = Assert.Single(body.Questions);
        Assert.Equal(1, question.QuestionId);
        Assert.Equal("1", question.Label);
        Assert.Equal("What is a primary key?", question.Text);
        Assert.False(question.AppearedRecently);
        var sitting = Assert.Single(question.Appearances);
        Assert.Equal(1, sitting.PaperId);
        Assert.Equal(2024, sitting.Year);
        Assert.Equal(6, sitting.Month);
    }

    [Fact]
    public async Task PaperQuestionsIncludeAppearancesAndARecentSignature()
    {
        await using var db = CreateDatabase();
        var now = DateTime.UtcNow;
        var first = now.AddMonths(-2);
        var second = now.AddMonths(-1);
        await SeedAsync(
            db,
            Paper(1, PaperStatus.Approved, 10, first.Year, first.Month, Utc(first.Year, first.Month, 1)),
            Paper(2, PaperStatus.Approved, 10, second.Year, second.Month, Utc(second.Year, second.Month, 1)));
        db.Questions.Add(new Question
        {
            Id = 1,
            SubjectId = 1,
            Text = "What is a primary key?",
            ContentHash = QuestionText.Hash("What is a primary key?"),
            CreatedAt = DateTime.UtcNow
        });
        db.PaperQuestions.AddRange(
            new PaperQuestion { PaperId = 1, QuestionId = 1, Ordinal = 1, Label = "1" },
            new PaperQuestion { PaperId = 2, QuestionId = 1, Ordinal = 1, Label = "Zadatak 1" });
        await db.SaveChangesAsync();

        var action = await CreateController(db, userId: 10, UserRole.User)
            .GetPaperQuestions(1, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var body = Assert.IsType<PaperQuestionsDto>(ok.Value);
        var question = Assert.Single(body.Questions);
        Assert.True(question.AppearedRecently);
        Assert.Equal(2, question.Appearances.Count);
        Assert.Contains(question.Appearances, sitting => sitting.PaperId == 2 && sitting.Label == "Zadatak 1");
    }

    [Fact]
    public async Task AnotherUserCannotReadQuestionsOnAPendingPaper()
    {
        await using var db = CreateDatabase();
        await SeedAsync(db, Paper(1, PaperStatus.Pending, 10, 2024, 6, Utc(2024, 1, 1)));

        var action = await CreateController(db, userId: 99, UserRole.User)
            .GetPaperQuestions(1, CancellationToken.None);

        var forbidden = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task StaffCanEditAQuestionWithoutRewritingOtherPapers()
    {
        await using var db = CreateDatabase();
        await SeedAsync(
            db,
            Paper(1, PaperStatus.Approved, 10, 2024, 6, Utc(2024, 1, 1)),
            Paper(2, PaperStatus.Approved, 10, 2024, 9, Utc(2024, 1, 2)));
        db.Questions.Add(new Question
        {
            Id = 1,
            SubjectId = 1,
            Text = "What is a primary key?",
            ContentHash = QuestionText.Hash("What is a primary key?"),
            CreatedAt = DateTime.UtcNow
        });
        db.PaperQuestions.AddRange(
            new PaperQuestion { PaperId = 1, QuestionId = 1, Ordinal = 1, Label = "1" },
            new PaperQuestion { PaperId = 2, QuestionId = 1, Ordinal = 1, Label = "1" });
        await db.SaveChangesAsync();

        var action = await CreateController(db, userId: 11, UserRole.Moderator)
            .UpdatePaperQuestion(
                1,
                1,
                new UpdatePaperQuestionRequest { Text = "What is a foreign key?" },
                CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var body = Assert.IsType<PaperQuestionsDto>(ok.Value);
        Assert.Equal("What is a foreign key?", Assert.Single(body.Questions).Text);
        Assert.Equal(2, await db.Questions.CountAsync());
        Assert.Equal(
            "What is a primary key?",
            (await db.PaperQuestions.Include(q => q.Question)
                .SingleAsync(q => q.PaperId == 2)).Question!.Text);
    }

    [Fact]
    public async Task StaffCanDeleteAQuestionAndCompactOrdinals()
    {
        await using var db = CreateDatabase();
        await SeedAsync(db, Paper(1, PaperStatus.Approved, 10, 2024, 6, Utc(2024, 1, 1)));
        db.Questions.AddRange(
            new Question
            {
                Id = 1,
                SubjectId = 1,
                Text = "One",
                ContentHash = QuestionText.Hash("One"),
                CreatedAt = DateTime.UtcNow
            },
            new Question
            {
                Id = 2,
                SubjectId = 1,
                Text = "Two",
                ContentHash = QuestionText.Hash("Two"),
                CreatedAt = DateTime.UtcNow
            });
        db.PaperQuestions.AddRange(
            new PaperQuestion { PaperId = 1, QuestionId = 1, Ordinal = 1, Label = "1" },
            new PaperQuestion { PaperId = 1, QuestionId = 2, Ordinal = 2, Label = "2" });
        await db.SaveChangesAsync();

        var action = await CreateController(db, userId: 11, UserRole.Moderator)
            .DeletePaperQuestion(1, 1, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var body = Assert.IsType<PaperQuestionsDto>(ok.Value);
        var remaining = Assert.Single(body.Questions);
        Assert.Equal(1, remaining.Ordinal);
        Assert.Equal("Two", remaining.Text);
        Assert.Equal(1, await db.Questions.CountAsync());
    }

    [Fact]
    public async Task StaffCanMergeABadSplit()
    {
        await using var db = CreateDatabase();
        await SeedAsync(db, Paper(1, PaperStatus.Approved, 10, 2024, 6, Utc(2024, 1, 1)));
        db.Questions.AddRange(
            new Question
            {
                Id = 1,
                SubjectId = 1,
                Text = "What is SQL?",
                ContentHash = QuestionText.Hash("What is SQL?"),
                CreatedAt = DateTime.UtcNow
            },
            new Question
            {
                Id = 2,
                SubjectId = 1,
                Text = "Give an example.",
                ContentHash = QuestionText.Hash("Give an example."),
                CreatedAt = DateTime.UtcNow
            },
            new Question
            {
                Id = 3,
                SubjectId = 1,
                Text = "Define a join.",
                ContentHash = QuestionText.Hash("Define a join."),
                CreatedAt = DateTime.UtcNow
            });
        db.PaperQuestions.AddRange(
            new PaperQuestion { PaperId = 1, QuestionId = 1, Ordinal = 1, Label = "1" },
            new PaperQuestion { PaperId = 1, QuestionId = 2, Ordinal = 2, Label = "2" },
            new PaperQuestion { PaperId = 1, QuestionId = 3, Ordinal = 3, Label = "3" });
        await db.SaveChangesAsync();

        var action = await CreateController(db, userId: 11, UserRole.Moderator)
            .MergePaperQuestions(
                1,
                new MergePaperQuestionsRequest { Ordinals = [1, 2] },
                CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var body = Assert.IsType<PaperQuestionsDto>(ok.Value);
        Assert.Equal(2, body.Questions.Count);
        Assert.Equal("What is SQL?\n\nGive an example.", body.Questions[0].Text);
        Assert.Equal("Define a join.", body.Questions[1].Text);
        Assert.Equal([1, 2], body.Questions.Select(q => q.Ordinal).ToArray());
    }

    [Fact]
    public async Task ReparsingAnApprovedPaperEnqueuesParsing()
    {
        await using var db = CreateDatabase();
        await SeedAsync(db, Paper(1, PaperStatus.Approved, 10, 2024, 6, Utc(2024, 1, 1)));
        var stored = await db.Papers.SingleAsync();
        stored.ParseStatus = PaperParseStatus.Parsed;
        await db.SaveChangesAsync();
        var queue = new RecordingPaperParseQueue();
        var controller = CreateController(db, userId: 11, UserRole.Moderator, parseQueue: queue);

        var action = await controller.ReparsePaper(1, CancellationToken.None);

        Assert.IsType<OkObjectResult>(action.Result);
        Assert.Equal(PaperParseStatus.Queued, (await db.Papers.SingleAsync()).ParseStatus);
        Assert.Equal([1], queue.Enqueued);
    }

    [Fact]
    public async Task ReparsingARejectedPaperReturnsConflict()
    {
        await using var db = CreateDatabase();
        await SeedAsync(db, Paper(1, PaperStatus.Rejected, 10, 2024, 6, Utc(2024, 1, 1)));

        var action = await CreateController(db, userId: 11, UserRole.Moderator)
            .ReparsePaper(1, CancellationToken.None);

        var conflict = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task YearOfStudyOutsideTheStudyLengthReturnsBadRequest()
    {
        await using var db = CreateDatabase();
        await db.Database.EnsureCreatedAsync();

        db.Majors.Add(new Major
        {
            Id = 1,
            NameSr = "Рачунарске науке",
            NameEn = "Computer Science",
            StudiesId = 1
        });
        await SeedAsync(db, Paper(1, PaperStatus.Approved, 10, 2024, 6, Utc(2024, 1, 1)));
        db.MajorSubjects.Add(new MajorSubject
        {
            MajorId = 1,
            SubjectId = 1,
            YearOfStudy = 3
        });
        await db.SaveChangesAsync();

        var action = await CreateController(db, userId: 11, UserRole.Moderator)
            .GetPapers(
                studiesId: 1,
                majorId: 1,
                yearOfStudy: 4,
                subjectId: 1,
                examType: null,
                month: null,
                year: null,
                paging: new PageRequest { PerPage = 100 },
                status: null,
                cancellationToken: CancellationToken.None);

        var result = Assert.IsType<ObjectResult>(action.Result);
        var problem = Assert.IsType<ValidationProblemDetails>(result.Value);
        Assert.Contains(
            "outside the 3 years of major 1",
            Assert.Single(problem.Errors["yearOfStudy"]));
    }

    [Fact]
    public async Task StudiesIdWithoutASubjectReturnsPapersFromThatStudy()
    {
        await using var db = await SeedCurriculumPapersAsync();

        var result = await ListAsync(
            CreateController(db, userId: 11, UserRole.Moderator),
            studiesId: 1);

        Assert.Equal(2, result.Data.Count);
        Assert.Contains(result.Data, p => p.Id == 1);
        Assert.Contains(result.Data, p => p.Id == 2);
        Assert.DoesNotContain(result.Data, p => p.Id == 3);
    }

    [Fact]
    public async Task MajorIdWithoutASubjectReturnsPapersFromThatMajor()
    {
        await using var db = await SeedCurriculumPapersAsync();

        var result = await ListAsync(
            CreateController(db, userId: 11, UserRole.Moderator),
            majorId: 2);

        var paper = Assert.Single(result.Data);
        Assert.Equal(2, paper.Id);
    }

    [Fact]
    public async Task YearOfStudyWithStudiesIdReturnsPapersFromThatYear()
    {
        await using var db = await SeedCurriculumPapersAsync();

        var result = await ListAsync(
            CreateController(db, userId: 11, UserRole.Moderator),
            studiesId: 1,
            yearOfStudy: 2);

        var paper = Assert.Single(result.Data);
        Assert.Equal(2, paper.Id);
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
        Assert.Empty(Attributes<AllowAnonymousAttribute>(typeof(PapersController), "GetPage"));
        Assert.Empty(Attributes<AllowAnonymousAttribute>(typeof(PapersController), "GetPaperQuestions"));

        var questionsPolicy = Attributes<AuthorizeAttribute>(typeof(PapersController), "GetPaperQuestions");
        Assert.DoesNotContain(questionsPolicy, attribute => attribute.Policy == RolePolicies.Staff);

        var deletePolicy = Attributes<AuthorizeAttribute>(typeof(PapersController), "DeletePaper");
        Assert.Contains(deletePolicy, attribute => attribute.Policy == RolePolicies.Staff);

        var updateQuestionPolicy = Attributes<AuthorizeAttribute>(typeof(PapersController), "UpdatePaperQuestion");
        Assert.Contains(updateQuestionPolicy, attribute => attribute.Policy == RolePolicies.Staff);

        var deleteQuestionPolicy = Attributes<AuthorizeAttribute>(typeof(PapersController), "DeletePaperQuestion");
        Assert.Contains(deleteQuestionPolicy, attribute => attribute.Policy == RolePolicies.Staff);

        var mergePolicy = Attributes<AuthorizeAttribute>(typeof(PapersController), "MergePaperQuestions");
        Assert.Contains(mergePolicy, attribute => attribute.Policy == RolePolicies.Staff);

        var reparsePolicy = Attributes<AuthorizeAttribute>(typeof(PapersController), "ReparsePaper");
        Assert.Contains(reparsePolicy, attribute => attribute.Policy == RolePolicies.Staff);

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
        UserRole role,
        PaperFileServer files = null!,
        IPaperParseQueue? parseQueue = null)
    {
        var user = new User { Id = userId, Username = $"user-{userId}", Role = role };
        var controller = new PapersController(
            db,
            storage: null!,
            files: files,
            pdfs: null!,
            submissions: null!,
            questions: new PaperQuestionService(db, NullLogger<PaperQuestionService>.Instance),
            parseQueue: parseQueue ?? new PaperParseQueue(),
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

    private static async Task<PagedResult<PaperDto>> ListAsync(
        PapersController controller,
        int? studiesId = null,
        int? majorId = null,
        int? yearOfStudy = null,
        int? subjectId = null)
    {
        var action = await controller.GetPapers(
            studiesId,
            majorId,
            yearOfStudy,
            subjectId,
            examType: null,
            month: null,
            year: null,
            paging: new PageRequest { PerPage = 100 },
            status: null,
            cancellationToken: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        return Assert.IsType<PagedResult<PaperDto>>(ok.Value);
    }

    private static async Task<ExamArchiveDbContext> SeedCurriculumPapersAsync()
    {
        var db = CreateDatabase();
        await db.Database.EnsureCreatedAsync();

        var bachelorSubject = new Subject { Id = 1, Code = "CS1", NameSr = "А", NameEn = "A" };
        var seSubject = new Subject { Id = 2, Code = "SE1", NameSr = "Б", NameEn = "B" };
        var masterSubject = new Subject { Id = 3, Code = "MS1", NameSr = "В", NameEn = "C" };

        db.Majors.AddRange(
            new Major { Id = 1, NameSr = "Рачунарске науке", NameEn = "Computer Science", StudiesId = 1 },
            new Major { Id = 2, NameSr = "Софтверско инжењерство", NameEn = "Software Engineering", StudiesId = 1 },
            new Major { Id = 3, NameSr = "Наука о подацима", NameEn = "Data Science", StudiesId = 2 });
        db.Subjects.AddRange(bachelorSubject, seSubject, masterSubject);
        db.MajorSubjects.AddRange(
            new MajorSubject { MajorId = 1, SubjectId = 1, YearOfStudy = 1 },
            new MajorSubject { MajorId = 2, SubjectId = 2, YearOfStudy = 2 },
            new MajorSubject { MajorId = 3, SubjectId = 3, YearOfStudy = 1 });

        var papers = new[]
        {
            Paper(1, PaperStatus.Approved, 10, 2024, 6, Utc(2024, 1, 1)),
            Paper(2, PaperStatus.Approved, 10, 2024, 6, Utc(2024, 1, 2)),
            Paper(3, PaperStatus.Approved, 10, 2024, 6, Utc(2024, 1, 3))
        };
        papers[0].SubjectId = 1;
        papers[0].Subject = bachelorSubject;
        papers[1].SubjectId = 2;
        papers[1].Subject = seSubject;
        papers[2].SubjectId = 3;
        papers[2].Subject = masterSubject;

        db.Papers.AddRange(papers);
        await db.SaveChangesAsync();
        return db;
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
}
