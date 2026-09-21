using ExamArchive.Data;
using ExamArchive.Models;
using Microsoft.EntityFrameworkCore;

namespace ExamArchive.Services;

public sealed class PaperParseService
{
    public const string NoExtractableText = "no-extractable-text";

    public const string NoNumberedQuestions = "no-numbered-questions";

    public const string ParseFailed = "parse-failed";

    private readonly ExamArchiveDbContext _db;
    private readonly PaperTextExtractor _extractor;
    private readonly QuestionSplitter _splitter;
    private readonly PaperQuestionService _questions;
    private readonly ILogger<PaperParseService> _logger;

    public PaperParseService(
        ExamArchiveDbContext db,
        PaperTextExtractor extractor,
        QuestionSplitter splitter,
        PaperQuestionService questions,
        ILogger<PaperParseService> logger)
    {
        _db = db;
        _extractor = extractor;
        _splitter = splitter;
        _questions = questions;
        _logger = logger;
    }

    public async Task ParseAsync(int paperId, CancellationToken cancellationToken)
    {
        var paper = await _db.Papers
            .Include(p => p.Files)
            .Include(p => p.PaperQuestions)
            .FirstOrDefaultAsync(p => p.Id == paperId, cancellationToken);

        if (paper is null)
        {
            return;
        }

        if (paper.Status != PaperStatus.Approved)
        {
            if (paper.ParseStatus == PaperParseStatus.Queued)
            {
                paper.ParseStatus = PaperParseStatus.NotQueued;
                paper.ParseError = null;
                await _db.SaveChangesAsync(cancellationToken);
            }

            return;
        }

        if (paper.ParseStatus == PaperParseStatus.Parsed)
        {
            return;
        }

        try
        {
            var extracted = _extractor.Extract(paper.Files);
            if (string.IsNullOrWhiteSpace(extracted))
            {
                await FinishAsync(
                    paper, PaperParseStatus.Skipped, NoExtractableText, cancellationToken);
                return;
            }

            var splits = _splitter.Split(extracted);
            if (splits.Count == 0)
            {
                await FinishAsync(
                    paper, PaperParseStatus.Skipped, NoNumberedQuestions, cancellationToken);
                return;
            }

            await StoreQuestionsAsync(paper, splits, cancellationToken);
            await FinishAsync(paper, PaperParseStatus.Parsed, parseError: null, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to parse questions from paper {PaperId}.", paperId);

            await FinishAsync(
                paper, PaperParseStatus.Failed, ParseFailed, cancellationToken);
        }
    }

    private async Task StoreQuestionsAsync(
        Paper paper,
        IReadOnlyList<SplitQuestion> splits,
        CancellationToken cancellationToken)
    {
        if (paper.PaperQuestions.Count > 0)
        {
            _db.PaperQuestions.RemoveRange(paper.PaperQuestions);
        }

        var unique = new List<(SplitQuestion Split, string Hash)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var split in splits)
        {
            var hash = QuestionText.Hash(split.Text);
            if (seen.Add(hash))
            {
                unique.Add((split, hash));
            }
        }

        var hashes = unique.Select(item => item.Hash).ToList();
        var existing = await _db.Questions
            .Where(q => q.SubjectId == paper.SubjectId && hashes.Contains(q.ContentHash))
            .ToListAsync(cancellationToken);
        var byHash = existing.ToDictionary(q => q.ContentHash, StringComparer.Ordinal);

        var ordinal = 1;
        foreach (var (split, hash) in unique)
        {
            if (!byHash.TryGetValue(hash, out var question))
            {
                question = new Question
                {
                    SubjectId = paper.SubjectId,
                    Text = split.Text,
                    ContentHash = hash,
                    CreatedAt = DateTime.UtcNow
                };

                _db.Questions.Add(question);
                byHash[hash] = question;
            }

            _db.PaperQuestions.Add(new PaperQuestion
            {
                Paper = paper,
                Question = question,
                Ordinal = ordinal,
                Label = TruncateLabel(split.Label)
            });

            ordinal++;
        }
    }

    private async Task FinishAsync(
        Paper paper,
        PaperParseStatus status,
        string? parseError,
        CancellationToken cancellationToken)
    {
        paper.ParseStatus = status;
        paper.ParseError = parseError;
        paper.ParsedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        if (status == PaperParseStatus.Parsed)
        {
            await _questions.RemoveUnusedAsync(cancellationToken);
        }
    }

    private static string TruncateLabel(string label) =>
        label.Length <= 50 ? label : label[..50];
}
