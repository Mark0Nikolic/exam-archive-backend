using ExamArchive.Data;
using ExamArchive.Dtos;
using ExamArchive.Models;
using Microsoft.EntityFrameworkCore;

namespace ExamArchive.Services;

public enum QuestionWriteStatus
{
    Ok,
    NotFound,
    Conflict,
    Invalid
}

public sealed record QuestionWriteResult(
    QuestionWriteStatus Status,
    string? Field = null,
    string? Message = null)
{
    public static QuestionWriteResult Ok() => new(QuestionWriteStatus.Ok);

    public static QuestionWriteResult Missing() => new(QuestionWriteStatus.NotFound);

    public static QuestionWriteResult Duplicate() => new(
        QuestionWriteStatus.Conflict,
        Field: "Text",
        Message: "This paper already contains that question.");

    public static QuestionWriteResult Invalid(string field, string message) =>
        new(QuestionWriteStatus.Invalid, field, message);
}

// Canonical questions plus the staff edits that fix a bad split. Parse writes
// through PaperParseService; this service is the read/repair path.
public sealed class PaperQuestionService
{
    public const int RecentAppearanceMonths = 12;

    private readonly ExamArchiveDbContext _db;
    private readonly ILogger<PaperQuestionService> _logger;

    public PaperQuestionService(
        ExamArchiveDbContext db,
        ILogger<PaperQuestionService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<PaperQuestionsDto?> GetVisibleAsync(
        int paperId,
        bool includeUnapproved,
        int? ownedByUserId,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var paper = await _db.Papers
            .AsNoTracking()
            .Where(p => p.Id == paperId)
            .Where(p =>
                includeUnapproved
                || p.Status == PaperStatus.Approved
                || (ownedByUserId != null && p.SubmittedByUserId == ownedByUserId))
            .Select(p => new
            {
                p.Id,
                p.ParseStatus,
                p.ParseError,
                Questions = p.PaperQuestions
                    .OrderBy(q => q.Ordinal)
                    .Select(q => new
                    {
                        q.QuestionId,
                        q.Ordinal,
                        q.Label,
                        Text = q.Question!.Text
                    })
                    .ToList()
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (paper is null)
        {
            return null;
        }

        var questionIds = paper.Questions.Select(q => q.QuestionId).Distinct().ToList();
        if (questionIds.Count == 0)
        {
            return new PaperQuestionsDto(
                paper.Id, paper.ParseStatus, paper.ParseError, []);
        }

        var appearances = await _db.PaperQuestions
            .AsNoTracking()
            .Where(pq =>
                questionIds.Contains(pq.QuestionId)
                && pq.Paper!.Status == PaperStatus.Approved)
            .Select(pq => new
            {
                pq.QuestionId,
                pq.PaperId,
                pq.Paper!.ExamType,
                pq.Paper.Month,
                pq.Paper.Year,
                pq.Ordinal,
                pq.Label
            })
            .ToListAsync(cancellationToken);

        var byQuestion = appearances.ToLookup(row => row.QuestionId);

        return new PaperQuestionsDto(
            paper.Id,
            paper.ParseStatus,
            paper.ParseError,
            [.. paper.Questions.Select(question =>
            {
                var sittings = byQuestion[question.QuestionId]
                    .OrderByDescending(row => row.Year)
                    .ThenByDescending(row => row.Month)
                    .ThenBy(row => row.PaperId)
                    .Select(row => new QuestionAppearanceDto(
                        row.PaperId,
                        row.ExamType,
                        row.Month,
                        row.Year,
                        row.Ordinal,
                        row.Label))
                    .ToList();

                var appearedRecently = sittings.Any(sitting =>
                    sitting.PaperId != paper.Id
                    && IsRecentSitting(sitting.Year, sitting.Month, utcNow));

                return new PaperQuestionDto(
                    question.QuestionId,
                    question.Ordinal,
                    question.Label,
                    question.Text,
                    sittings,
                    appearedRecently);
            })]);
    }

    public async Task<QuestionWriteResult> UpdateOccurrenceAsync(
        int paperId,
        int ordinal,
        string? label,
        string? text,
        CancellationToken cancellationToken)
    {
        var paper = await LoadPaperWithQuestionsAsync(paperId, cancellationToken);
        if (paper is null)
        {
            return QuestionWriteResult.Missing();
        }

        var occurrence = paper.PaperQuestions.FirstOrDefault(q => q.Ordinal == ordinal);
        if (occurrence is null)
        {
            return QuestionWriteResult.Missing();
        }

        if (label is not null)
        {
            occurrence.Label = label.Trim();
        }

        if (text is not null)
        {
            var trimmed = text.Trim();
            var attached = await AttachCanonicalAsync(
                paper, occurrence, trimmed, cancellationToken);

            if (attached.Status != QuestionWriteStatus.Ok)
            {
                return attached;
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        await RemoveUnusedAsync(cancellationToken);

        _logger.LogInformation(
            "Edited question ordinal {Ordinal} on paper {PaperId}.", ordinal, paperId);

        return QuestionWriteResult.Ok();
    }

    public async Task<QuestionWriteResult> DeleteOccurrenceAsync(
        int paperId,
        int ordinal,
        CancellationToken cancellationToken)
    {
        var paper = await LoadPaperWithQuestionsAsync(paperId, cancellationToken);
        if (paper is null)
        {
            return QuestionWriteResult.Missing();
        }

        var occurrence = paper.PaperQuestions.FirstOrDefault(q => q.Ordinal == ordinal);
        if (occurrence is null)
        {
            return QuestionWriteResult.Missing();
        }

        _db.PaperQuestions.Remove(occurrence);
        await _db.SaveChangesAsync(cancellationToken);

        await CompactOrdinalsAsync(paper, cancellationToken);
        await RemoveUnusedAsync(cancellationToken);

        _logger.LogInformation(
            "Deleted question ordinal {Ordinal} on paper {PaperId}.", ordinal, paperId);

        return QuestionWriteResult.Ok();
    }

    public async Task<QuestionWriteResult> MergeOccurrencesAsync(
        int paperId,
        IReadOnlyList<int> ordinals,
        CancellationToken cancellationToken)
    {
        var paper = await LoadPaperWithQuestionsAsync(paperId, cancellationToken);
        if (paper is null)
        {
            return QuestionWriteResult.Missing();
        }

        var selected = new List<PaperQuestion>(ordinals.Count);
        foreach (var ordinal in ordinals)
        {
            var occurrence = paper.PaperQuestions.FirstOrDefault(q => q.Ordinal == ordinal);
            if (occurrence is null)
            {
                return QuestionWriteResult.Invalid(
                    nameof(MergePaperQuestionsRequest.Ordinals),
                    $"This paper has no question with ordinal {ordinal}.");
            }

            selected.Add(occurrence);
        }

        var mergedText = string.Join(
            "\n\n",
            selected.Select(occurrence => occurrence.Question!.Text));
        var survivor = selected[0];
        var attached = await AttachCanonicalAsync(
            paper, survivor, mergedText, cancellationToken, selected);

        if (attached.Status != QuestionWriteStatus.Ok)
        {
            return attached;
        }

        _db.PaperQuestions.RemoveRange(selected.Skip(1));
        await _db.SaveChangesAsync(cancellationToken);

        await CompactOrdinalsAsync(paper, cancellationToken);
        await RemoveUnusedAsync(cancellationToken);

        _logger.LogInformation(
            "Merged ordinals {Ordinals} on paper {PaperId}.",
            string.Join(", ", ordinals),
            paperId);

        return QuestionWriteResult.Ok();
    }

    public async Task RemoveUnusedAsync(CancellationToken cancellationToken)
    {
        var orphans = await _db.Questions
            .Where(question => !question.PaperQuestions.Any())
            .ToListAsync(cancellationToken);

        if (orphans.Count == 0)
        {
            return;
        }

        _db.Questions.RemoveRange(orphans);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public static bool IsRecentSitting(int year, int month, DateTime utcNow)
    {
        var sitting = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var cutoff = new DateTime(utcNow.Year, utcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc)
            .AddMonths(-RecentAppearanceMonths);

        return sitting >= cutoff;
    }

    private async Task<Paper?> LoadPaperWithQuestionsAsync(
        int paperId,
        CancellationToken cancellationToken) =>
        await _db.Papers
            .Include(p => p.PaperQuestions)
                .ThenInclude(q => q.Question)
            .FirstOrDefaultAsync(p => p.Id == paperId, cancellationToken);

    // Retargets this paper's occurrence onto the canonical row for `text`. A
    // shared question is not rewritten in place: February's wording stays
    // February's if a moderator is only fixing September's split.
    private async Task<QuestionWriteResult> AttachCanonicalAsync(
        Paper paper,
        PaperQuestion occurrence,
        string text,
        CancellationToken cancellationToken,
        IReadOnlyList<PaperQuestion>? merging = null)
    {
        var hash = QuestionText.Hash(text);
        var current = occurrence.Question!;
        var mergingIds = merging?.Select(item => item.Id).ToHashSet() ?? [];

        var alreadyOnPaper = paper.PaperQuestions.FirstOrDefault(item =>
            item.Id != occurrence.Id
            && !mergingIds.Contains(item.Id)
            && item.Question is not null
            && item.Question.ContentHash == hash);

        if (alreadyOnPaper is not null)
        {
            return QuestionWriteResult.Duplicate();
        }

        if (current.ContentHash == hash)
        {
            return QuestionWriteResult.Ok();
        }

        var existing = await _db.Questions
            .FirstOrDefaultAsync(
                question => question.SubjectId == paper.SubjectId
                    && question.ContentHash == hash,
                cancellationToken);

        if (existing is not null)
        {
            occurrence.Question = existing;
            return QuestionWriteResult.Ok();
        }

        var soleUse = paper.PaperQuestions.Count(item => item.QuestionId == current.Id)
            + await _db.PaperQuestions.CountAsync(
                item => item.QuestionId == current.Id && item.PaperId != paper.Id,
                cancellationToken);

        if (soleUse == 1)
        {
            current.Text = text;
            current.ContentHash = hash;
            return QuestionWriteResult.Ok();
        }

        var created = new Question
        {
            SubjectId = paper.SubjectId,
            Text = text,
            ContentHash = hash,
            CreatedAt = DateTime.UtcNow
        };

        _db.Questions.Add(created);
        occurrence.Question = created;
        return QuestionWriteResult.Ok();
    }

    // Unique (PaperId, Ordinal) cannot swap in one UPDATE, so the values move
    // to a vacant range first and then back down to 1..n.
    private async Task CompactOrdinalsAsync(Paper paper, CancellationToken cancellationToken)
    {
        await _db.Entry(paper).Collection(p => p.PaperQuestions).LoadAsync(cancellationToken);

        var remaining = paper.PaperQuestions
            .OrderBy(item => item.Ordinal)
            .ToList();

        if (remaining.Count == 0)
        {
            return;
        }

        for (var i = 0; i < remaining.Count; i++)
        {
            remaining[i].Ordinal = i + 1_000;
        }

        await _db.SaveChangesAsync(cancellationToken);

        for (var i = 0; i < remaining.Count; i++)
        {
            remaining[i].Ordinal = i + 1;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }
}
