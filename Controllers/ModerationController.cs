using ExamArchive.Data;
using ExamArchive.Dtos;
using ExamArchive.Models;
using ExamArchive.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ExamArchive.Controllers;

/// <summary>
/// Staff-facing review of submitted papers.
/// </summary>
/// <remarks>
/// Split from <see cref="PapersController"/> along the line of who may call it:
/// that controller is public and largely anonymous, this one is staff-only. Keeping
/// them apart is what makes the [Authorize] below a single attribute on the class
/// rather than one per action, where a single omission on a newly added endpoint
/// would leave a hole that nothing would report.
/// <para>
/// The role list is the whole authorization model today. Admin is named separately
/// from Moderator not because they differ here — they do not — but because the
/// operations that will separate them, editing the subject catalogue and managing
/// accounts, are the next things to be built.
/// </para>
/// </remarks>
[ApiController]
[Route("api/moderation")]
[Produces("application/json")]
[Authorize(Policy = RolePolicies.Staff)]
public class ModerationController : ControllerBase
{
    private readonly ExamArchiveDbContext _db;
    private readonly PaperFileServer _files;
    private readonly PaperFileStorage _storage;
    private readonly PaperSubmissionService _submissions;
    private readonly ILogger<ModerationController> _logger;

    public ModerationController(
        ExamArchiveDbContext db,
        PaperFileServer files,
        PaperFileStorage storage,
        PaperSubmissionService submissions,
        ILogger<ModerationController> logger)
    {
        _db = db;
        _files = files;
        _storage = storage;
        _submissions = submissions;
        _logger = logger;
    }

    /// <summary>
    /// Lists papers awaiting review, longest-waiting first.
    /// </summary>
    /// <param name="status">
    /// Which queue to read. Defaults to <see cref="PaperStatus.Pending"/> — the
    /// work list. Passing Approved or Rejected reviews past decisions.
    /// </param>
    /// <remarks>
    /// Returns unapproved and rejected submissions, which is the one thing the
    /// browse API deliberately never does — the class-level [Authorize] is what
    /// keeps that distinction true.
    /// </remarks>
    [HttpGet("papers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResult<ModerationPaperDto>>> GetPapers(
        [FromQuery] PageRequest paging,
        [FromQuery] PaperStatus status = PaperStatus.Pending,
        CancellationToken cancellationToken = default)
    {
        // Oldest first, the opposite of the browse API. This is a work queue, so
        // the paper that has been waiting longest is the one to deal with next;
        // newest-first would let an old submission sit at the bottom forever.
        var papers = await _db.Papers
            .AsNoTracking()
            .Where(p => p.Status == status)
            .OrderBy(p => p.UploadedAt)
            .ThenBy(p => p.Id)
            .Select(p => new ModerationPaperDto(
                p.Id,
                p.SubjectId,
                p.Subject!.NameSr,
                p.ExamType,
                p.Month,
                p.Year,
                p.Files.Count,
                p.UploadedAt,
                p.Status,
                p.ReviewedAt,
                p.RejectionReason))
            .ToPagedResultAsync(paging, cancellationToken);

        return Ok(papers);
    }

    /// <summary>
    /// Lists the pages of a submitted paper, whatever its status.
    /// </summary>
    /// <remarks>
    /// The moderation counterpart to the browse listing: a reviewer needs the page
    /// count and formats before opening anything, and unlike the public API this
    /// answers for pending and rejected papers too.
    /// </remarks>
    [HttpGet("papers/{id:int}/files")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IEnumerable<PaperFileDto>>> GetPaperFiles(
        int id,
        CancellationToken cancellationToken)
    {
        var files = await _files.ListAsync(id, approvedOnly: false, cancellationToken);

        return files is null ? NotFound() : Ok(files);
    }

    /// <summary>
    /// Opens one page of a submitted paper for review, in the browser.
    /// </summary>
    /// <param name="id">The paper being reviewed.</param>
    /// <param name="pageNumber">Which page, starting at 1.</param>
    /// <param name="download">True to save the file instead of viewing it.</param>
    /// <remarks>
    /// Served inline so review is a matter of opening a tab rather than
    /// downloading, opening, and then deleting a file per submission.
    /// <para>
    /// SECURITY: this returns unreviewed, submitter-supplied bytes, which is the
    /// riskiest thing the application does. The response carries
    /// X-Content-Type-Options: nosniff so a browser cannot decide the file is
    /// really HTML and execute it against this origin.
    /// </para>
    /// </remarks>
    [HttpGet("papers/{id:int}/files/{pageNumber:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPaperFile(
        int id,
        int pageNumber,
        [FromQuery] bool download,
        CancellationToken cancellationToken)
    {
        return await _files.ServeAsync(
            Response,
            id,
            pageNumber,
            approvedOnly: false,
            asAttachment: download,
            cancellationToken);
    }

    /// <summary>
    /// Publishes a paper: it becomes visible to everyone through the browse API.
    /// </summary>
    /// <remarks>
    /// Also reverses a rejection, which clears the stored reason — leaving it
    /// behind would put a rejection note on a published paper. Approving a paper
    /// that is already approved changes nothing and still returns 200, so a
    /// double-clicked button is not an error.
    /// </remarks>
    [HttpPost("papers/{id:int}/approve")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ModerationPaperDto>> ApprovePaper(
        int id,
        CancellationToken cancellationToken)
    {
        var paper = await _db.Papers.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (paper is null)
        {
            return NotFound();
        }

        if (paper.Status != PaperStatus.Approved)
        {
            paper.Status = PaperStatus.Approved;
            paper.ReviewedAt = DateTime.UtcNow;
            paper.RejectionReason = null;

            await _db.SaveChangesAsync(cancellationToken);
        }

        return Ok(await LoadForModerationAsync(id, cancellationToken));
    }

    /// <summary>
    /// Turns a paper down, recording why.
    /// </summary>
    /// <remarks>
    /// The files stay on disk. A rejection is a judgement that can be revisited —
    /// a moderator misreads a page, or the submitter explains what it is — and
    /// deleting on rejection would make that unrecoverable. Papers are small; the
    /// safe direction is to keep the bytes and sweep them later if it ever matters.
    /// <para>
    /// Rejecting an already-rejected paper updates the reason, which is what a
    /// moderator correcting their own wording expects.
    /// </para>
    /// </remarks>
    [HttpPost("papers/{id:int}/reject")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ModerationPaperDto>> RejectPaper(
        int id,
        [FromBody] RejectPaperRequest request,
        CancellationToken cancellationToken)
    {
        var paper = await _db.Papers.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (paper is null)
        {
            return NotFound();
        }

        paper.Status = PaperStatus.Rejected;
        paper.ReviewedAt = DateTime.UtcNow;
        paper.RejectionReason = request.Reason.Trim();

        await _db.SaveChangesAsync(cancellationToken);

        return Ok(await LoadForModerationAsync(id, cancellationToken));
    }

    /// <summary>
    /// Corrects a paper's filing details: its subject, sitting, month and year.
    /// </summary>
    /// <remarks>
    /// The one gap the archive had until now. A paper filed under the wrong subject
    /// or year is not wrong enough to reject — the scan is fine and the submitter
    /// did nothing wrong — but with no way to edit it, the only remedies were to
    /// leave it misfiled or to reject and re-upload it, and neither is honest.
    /// <para>
    /// Moderator rather than Admin. This is a judgement about one paper, which is
    /// exactly what a moderator is for; the subject catalogue it files against is
    /// the admin's.
    /// </para>
    /// </remarks>
    [HttpPut("papers/{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ModerationPaperDto>> UpdatePaper(
        int id,
        [FromBody] UpdatePaperRequest request,
        CancellationToken cancellationToken)
    {
        var paper = await _db.Papers.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (paper is null)
        {
            return NotFound();
        }

        // Checked here for a usable message. The foreign key would refuse it anyway,
        // but as a 500 that says nothing about which field was wrong.
        var subjectExists = await _db.Subjects
            .AnyAsync(subject => subject.Id == request.SubjectId, cancellationToken);

        if (!subjectExists)
        {
            ModelState.AddModelError(
                nameof(request.SubjectId),
                $"No subject with id {request.SubjectId} exists.");

            return ValidationProblem(ModelState);
        }

        paper.SubjectId = request.SubjectId;
        paper.ExamType = request.ExamType;
        paper.Month = request.Month;
        paper.Year = request.Year;

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("{Moderator} edited paper {PaperId}.", User.Identity?.Name, id);

        return Ok(await LoadForModerationAsync(id, cancellationToken));
    }

    /// <summary>
    /// Removes a paper from the archive entirely, along with its files.
    /// </summary>
    /// <remarks>
    /// Distinct from rejection, which is a decision about whether to publish and
    /// stays reversible. This is for papers that should not exist at all —
    /// duplicates, a submission somebody asks to have withdrawn, a scan carrying
    /// something personal — and it is not reversible, which is why it is the one
    /// action here restricted to an administrator.
    /// <para>
    /// The PaperFiles rows go with it through the cascade configured on the foreign
    /// key. The bytes on disk do not: cascade deletes rows, and a paper deleted
    /// without this call would leak its files silently.
    /// </para>
    /// </remarks>
    [HttpDelete("papers/{id:int}")]
    [Authorize(Policy = RolePolicies.Administrators)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeletePaper(
        int id,
        CancellationToken cancellationToken)
    {
        var paper = await _db.Papers
            .Include(p => p.Files)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (paper is null)
        {
            return NotFound();
        }

        // Captured before the delete, because the rows about to go are the only
        // record of where the bytes live.
        var storedPaths = paper.Files.Select(f => f.StoredPath).ToList();

        _db.Papers.Remove(paper);
        await _db.SaveChangesAsync(cancellationToken);

        // Files after the commit, deliberately. Deleting them first and then failing
        // to save would leave rows pointing at nothing, which breaks the archive;
        // this order can at worst leave bytes with no row, which is only wasted disk.
        _storage.TryDeleteOrphans(storedPaths, _logger);

        _logger.LogInformation(
            "{Admin} deleted paper {PaperId} and its {FileCount} file(s).",
            User.Identity?.Name,
            id,
            storedPaths.Count);

        return NoContent();
    }

    /// <summary>
    /// Uploads a paper on behalf of staff, published immediately.
    /// </summary>
    /// <remarks>
    /// Identical to the public upload except that the paper skips the queue: a
    /// professor or assistant adding a paper is the same authority that would have
    /// approved it, so making them upload it and then approve their own submission
    /// is a step that decides nothing.
    /// <para>
    /// SECURITY: this endpoint publishes to the archive without review, which makes
    /// it the most valuable thing here to an attacker. It lives on this controller
    /// precisely so that the class-level [Authorize] covers it.
    /// </para>
    /// </remarks>
    [HttpPost("papers/upload")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(PaperSubmissionService.MaxTotalUploadBytes)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<UploadedPaperDto>> UploadPaper(
        [FromForm] UploadPaperRequest request,
        CancellationToken cancellationToken)
    {
        // Recorded against the staff member who uploaded it. This is the one path
        // into the archive that nobody reviews, so it is the one path where a name
        // should be attached — public submissions are anonymous and answer for
        // themselves through a moderator's decision.
        var result = await _submissions.SubmitAsync(
            request, PaperStatus.Approved, User.GetUserId(), cancellationToken);

        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(error.Field, error.Message);
            }

            return ValidationProblem(ModelState);
        }

        // Unlike the public upload this one has somewhere to point: the paper is
        // approved, so it is already reachable through the browse API.
        return CreatedAtAction(
            actionName: nameof(PapersController.GetPaperFiles),
            controllerName: "Papers",
            routeValues: new { id = result.Paper!.Id },
            value: UploadedPaperDto.From(result.Paper));
    }

    /// <summary>
    /// Re-reads a paper in the shape the queue uses, so a decision returns the
    /// same row the caller was already displaying.
    /// </summary>
    private async Task<ModerationPaperDto?> LoadForModerationAsync(
        int id,
        CancellationToken cancellationToken)
    {
        return await _db.Papers
            .AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new ModerationPaperDto(
                p.Id,
                p.SubjectId,
                p.Subject!.NameSr,
                p.ExamType,
                p.Month,
                p.Year,
                p.Files.Count,
                p.UploadedAt,
                p.Status,
                p.ReviewedAt,
                p.RejectionReason))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
