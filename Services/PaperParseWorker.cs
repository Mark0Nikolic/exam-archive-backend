using ExamArchive.Data;
using ExamArchive.Models;
using Microsoft.EntityFrameworkCore;

namespace ExamArchive.Services;

public sealed class PaperParseWorker : BackgroundService
{
    private readonly IPaperParseQueue _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PaperParseWorker> _logger;

    public PaperParseWorker(
        IPaperParseQueue queue,
        IServiceScopeFactory scopes,
        ILogger<PaperParseWorker> logger)
    {
        _queue = queue;
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await BackfillAsync(stoppingToken);

        await foreach (var paperId in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var parser = scope.ServiceProvider.GetRequiredService<PaperParseService>();
                await parser.ParseAsync(paperId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Parse worker failed for paper {PaperId}.", paperId);
            }
        }
    }

    private async Task BackfillAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExamArchiveDbContext>();

        var pending = await db.Papers
            .AsNoTracking()
            .Where(p => p.Status == PaperStatus.Approved
                && (p.ParseStatus == PaperParseStatus.NotQueued
                    || p.ParseStatus == PaperParseStatus.Queued))
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);

        foreach (var paperId in pending)
        {
            _queue.Enqueue(paperId);
        }

        if (pending.Count > 0)
        {
            _logger.LogInformation(
                "Queued {Count} approved paper(s) that still need question parsing.",
                pending.Count);
        }
    }
}
