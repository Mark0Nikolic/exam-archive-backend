using ExamArchive.Data;
using ExamArchive.Dtos;
using ExamArchive.Models;
using ExamArchive.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ExamArchive.Controllers;

/// <summary>
/// The public face of the archive: browsing approved papers, submitting new ones,
/// and checking on a submission.
/// </summary>
/// <remarks>
/// Reading is anonymous and writing is not: browsing and downloading take no
/// account, submitting takes any account. That is still narrower than
/// <see cref="ModerationController"/>, where <c>[Authorize]</c> sits on the class
/// and every action demands a staff role. Each controller defaults to what is safe
/// for the audience it serves, which is why they remain separate types rather than
/// one controller with a mix of attributes.
/// </remarks>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class PapersController : ControllerBase
{
    private readonly ExamArchiveDbContext _db;
    private readonly PaperFileServer _files;
    private readonly PaperSubmissionService _submissions;

    public PapersController(
        ExamArchiveDbContext db,
        PaperFileServer files,
        PaperSubmissionService submissions)
    {
        _db = db;
        _files = files;
        _submissions = submissions;
    }

    /// <summary>
    /// Lists approved exam papers, newest exam first.
    /// </summary>
    /// <param name="subjectId">
    /// Restricts the list to one subject. Optional: omitted, the whole archive is
    /// browsed, which is what a landing page showing recent additions needs.
    /// </param>
    /// <param name="limit">
    /// How many papers to return, clamped to a sane range. Present because the
    /// unfiltered list grows without bound as the archive fills, and an endpoint
    /// that returns every row eventually returns too many.
    /// </param>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IEnumerable<PaperDto>>> GetPapers(
        [FromQuery] int? subjectId,
        CancellationToken cancellationToken,
        [FromQuery] int limit = 100)
    {
        var query = _db.Papers
            .AsNoTracking()
            .Where(p => p.Status == PaperStatus.Approved);

        if (subjectId is not null)
        {
            // With this applied, filtering on SubjectId then ordering by Year/Month
            // matches the IX_Papers_SubjectId_Year_Month index exactly.
            query = query.Where(p => p.SubjectId == subjectId);
        }

        var papers = await query
            .OrderByDescending(p => p.Year)
            .ThenByDescending(p => p.Month)

            // Id last so the order is total. Without it two papers sat in the same
            // month come back in whatever order the server felt like, which makes a
            // list appear to reshuffle between identical requests.
            .ThenByDescending(p => p.Id)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(p => new PaperDto(
                p.Id,
                p.SubjectId,
                p.Subject!.NameSr,
                p.Subject.NameEn,
                p.ExamType,
                p.Month,
                p.Year,
                p.Files.Count,
                p.UploadedAt))
            .ToListAsync(cancellationToken);

        return Ok(papers);
    }

    /// <summary>
    /// Fetches one approved paper.
    /// </summary>
    /// <remarks>
    /// A detail page needs this: reaching a paper by its own URL is otherwise
    /// impossible without listing its whole subject and searching the result.
    /// <para>
    /// Unapproved papers are 404, not 403, matching the file endpoints below —
    /// telling the two apart would let anyone probe ids to learn that a pending
    /// paper exists, which is the thing keeping it off this API prevents.
    /// </para>
    /// </remarks>
    [HttpGet("{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PaperDto>> GetPaper(
        int id,
        CancellationToken cancellationToken)
    {
        var paper = await _db.Papers
            .AsNoTracking()
            .Where(p => p.Id == id && p.Status == PaperStatus.Approved)
            .Select(p => new PaperDto(
                p.Id,
                p.SubjectId,
                p.Subject!.NameSr,
                p.Subject.NameEn,
                p.ExamType,
                p.Month,
                p.Year,
                p.Files.Count,
                p.UploadedAt))
            .FirstOrDefaultAsync(cancellationToken);

        return paper is null ? NotFound() : Ok(paper);
    }

    /// <summary>
    /// Lists the pages of an approved paper, in reading order.
    /// </summary>
    /// <remarks>
    /// A client needs this before fetching anything: it says how many pages there
    /// are and what each one is, so an image viewer and a PDF viewer can be chosen
    /// per page rather than guessed at.
    /// </remarks>
    [HttpGet("{id:int}/files")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IEnumerable<PaperFileDto>>> GetPaperFiles(
        int id,
        CancellationToken cancellationToken)
    {
        var files = await _files.ListAsync(id, approvedOnly: true, cancellationToken);

        return files is null ? NotFound() : Ok(files);
    }

    /// <summary>
    /// Serves one page of an approved paper.
    /// </summary>
    /// <param name="id">The paper.</param>
    /// <param name="pageNumber">Which page, starting at 1.</param>
    /// <param name="download">
    /// True to force a save dialog. Left false the file opens in the browser,
    /// which is what a student reading an archived paper wants.
    /// </param>
    [HttpGet("{id:int}/files/{pageNumber:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPaperFile(
        int id,
        int pageNumber,
        [FromQuery] bool download,
        CancellationToken cancellationToken)
    {
        // Same 404 whether the paper does not exist or is merely unapproved: a
        // distinct response would let anyone probe ids to discover that a pending
        // paper exists, which is exactly what keeping it off the browse API prevents.
        return await _files.ServeAsync(
            Response,
            id,
            pageNumber,
            approvedOnly: true,
            asAttachment: download,
            cancellationToken);
    }

    /// <summary>
    /// Submits an exam paper to the archive. Files are stored on disk and a paper
    /// row is created with status "Pending" — it stays out of the browse API until
    /// a moderator approves it.
    /// </summary>
    /// <remarks>
    /// Requires an account, which is a change from the archive's original design.
    /// Anonymity was worth something to a submitter who would rather not be named
    /// as the source of a circulating exam paper, and it is given up because an
    /// endpoint open to everyone accepts spam and deliberately misleading files at
    /// the same rate it accepts papers, and a moderator sorting those by hand is a
    /// worse outcome than a sign-in prompt.
    /// <para>
    /// Any signed-in account may submit, not only students. A moderator has a
    /// faster route in — see <see cref="ModerationController.UploadPaper"/>, which
    /// skips the queue — but nothing here needs to refuse them.
    /// </para>
    /// </remarks>
    [HttpPost("upload")]
    [Authorize]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(PaperSubmissionService.MaxTotalUploadBytes)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<UploadedPaperDto>> UploadPaper(
        [FromForm] UploadPaperRequest request,
        CancellationToken cancellationToken)
    {
        // Pending regardless of who is calling: an account proves somebody can be
        // held to a submission, not that the submission is any good, so everything
        // through this endpoint still waits for a moderator. The path that skips the
        // queue is a separate, authorized endpoint on ModerationController, not a
        // branch in this one.
        //
        // [Authorize] has already established there is a principal, so the id is
        // present — but it is passed as the nullable it is rather than asserted,
        // because a cookie from an older sign-in could satisfy the attribute without
        // carrying the claim.
        var result = await _submissions.SubmitAsync(
            request, PaperStatus.Pending, User.GetUserId(), cancellationToken);

        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(error.Field, error.Message);
            }

            return ValidationProblem(ModelState);
        }

        // 201 without a Location header: a pending paper has no address yet. It is
        // deliberately absent from GET /api/papers, and pointing at that list would
        // send the caller somewhere their paper does not appear.
        return StatusCode(
            StatusCodes.Status201Created,
            UploadedPaperDto.From(result.Paper!, result.ClaimToken));
    }

    /// <summary>
    /// Lists the caller's own submissions, newest first, whatever their status.
    /// </summary>
    /// <remarks>
    /// The reason an account is worth having. A rejection reason is written for the
    /// submitter, and until now the only way to read it was the claim code handed
    /// out at upload — one code per paper, shown once, unrecoverable if lost. An
    /// account holds every submission instead, so a submitter who loses track of a
    /// paper can still find out what happened to it.
    /// <para>
    /// Pending and rejected papers appear here and nowhere else on this controller.
    /// That is not a leak of the moderation queue: the filter is the caller's own
    /// id, so this shows a submitter their own work and no one else's.
    /// </para>
    /// </remarks>
    [HttpGet("mine")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IEnumerable<SubmissionStatusDto>>> GetMySubmissions(
        CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();

        if (userId is null)
        {
            return Unauthorized();
        }

        // Backed by IX_Papers_SubmittedByUserId.
        var submissions = await _db.Papers
            .AsNoTracking()
            .Where(p => p.SubmittedByUserId == userId)
            .OrderByDescending(p => p.UploadedAt)
            .ThenByDescending(p => p.Id)
            .Select(p => new SubmissionStatusDto(
                p.Id,
                p.Subject!.NameSr,
                p.ExamType,
                p.Month,
                p.Year,
                p.UploadedAt,
                p.Status,
                p.ReviewedAt,
                p.RejectionReason))
            .ToListAsync(cancellationToken);

        return Ok(submissions);
    }

    /// <summary>
    /// Reports what happened to a submission, given the claim code issued when it
    /// was uploaded.
    /// </summary>
    /// <param name="token">
    /// The code from the upload response. Case, dashes and the letters the alphabet
    /// avoids are all forgiven, so it can be typed as it was written down.
    /// </param>
    /// <remarks>
    /// This is what replaces an account. A rejection reason is written for the
    /// submitter, and without somewhere to read it the field is only ever seen by
    /// the moderator who typed it — the submitter learns nothing and uploads the
    /// same unreadable photo again.
    /// <para>
    /// The code is the only credential, so anyone holding it sees this. That is
    /// acceptable because of how little it reveals: a subject, a date, and a note
    /// about photo quality, on a paper the holder of the code almost certainly
    /// submitted. It is not a way into the archive — the files are reached through
    /// the browse API, which still refuses anything unapproved.
    /// </para>
    /// </remarks>
    [HttpGet("status/{token}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SubmissionStatusDto>> GetSubmissionStatus(
        string token,
        CancellationToken cancellationToken)
    {
        // Hashed before it touches the query, so the lookup is an index seek on a
        // fixed-length value and the code itself never appears in a logged
        // parameter or a query plan.
        var hash = ClaimToken.Hash(token);

        var submission = await _db.Papers
            .AsNoTracking()
            .Where(p => p.ClaimTokenHash == hash)
            .Select(p => new SubmissionStatusDto(
                p.Id,
                p.Subject!.NameSr,
                p.ExamType,
                p.Month,
                p.Year,
                p.UploadedAt,
                p.Status,
                p.ReviewedAt,
                p.RejectionReason))
            .FirstOrDefaultAsync(cancellationToken);

        // A malformed code and a well-formed one that matches nothing get the same
        // answer. There is nothing to gain from telling them apart, and validating
        // the shape first would confirm the format to anyone probing.
        return submission is null ? NotFound() : Ok(submission);
    }
}
