using System.ComponentModel.DataAnnotations;
using ExamArchive.Data;
using ExamArchive.Dtos;
using ExamArchive.Models;
using ExamArchive.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ExamArchive.Controllers;

// Everything the archive does with papers: browsing, submitting, and — for staff —
// reviewing and deciding.
//
// One controller for one resource, so it cannot default to "staff only": the
// class-level attribute is the weaker [Authorize], and an endpoint added here
// without an explicit policy is reachable by any signed-in account. Every staff
// action below names its policy.
//
// Visibility is decided inside each read query from the caller's role, id and the
// paper's owner. ClaimsPrincipalExtensions.IsStaff keeps the staff definition aligned
// with the policies that protect moderation actions.
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
[Authorize]
public class PapersController : ControllerBase
{
    private readonly ExamArchiveDbContext _db;
    private readonly PaperFileStorage _storage;
    private readonly PaperSubmissionService _submissions;
    private readonly ILogger<PapersController> _logger;

    public PapersController(
        ExamArchiveDbContext db,
        PaperFileStorage storage,
        PaperSubmissionService submissions,
        ILogger<PapersController> logger)
    {
        _db = db;
        _storage = storage;
        _submissions = submissions;
        _logger = logger;
    }

    // Lists every paper this caller is allowed to know about. Staff see the full
    // archive; an ordinary account sees the approved archive plus its own pending
    // and rejected submissions.
    //
    // studiesId, majorId, yearOfStudy and subjectId all filter, and any prefix of
    // that chain is enough — choosing a study with "all majors" still narrows the
    // grid. A contradiction between them is a 400, see ValidateCascadeAsync.
    // examType, month and year (the sitting) are independent of that chain.
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PagedResult<PaperDto>>> GetPapers(
        [FromQuery, Range(1, int.MaxValue, ErrorMessage = "StudiesId must be a positive id.")]
        int? studiesId,
        [FromQuery, Range(1, int.MaxValue, ErrorMessage = "MajorId must be a positive id.")]
        int? majorId,
        [FromQuery, Range(1, int.MaxValue, ErrorMessage = "YearOfStudy must be at least 1.")]
        int? yearOfStudy,
        [FromQuery, Range(1, int.MaxValue, ErrorMessage = "SubjectId must be a positive id.")]
        int? subjectId,
        [FromQuery] ExamType? examType,
        [FromQuery, Range(1, 12, ErrorMessage = "Month must be between 1 and 12.")]
        int? month,
        [FromQuery] int? year,
        [FromQuery] PageRequest paging,
        [FromQuery] PaperStatus? status,
        CancellationToken cancellationToken = default)
    {
        // [Authorize] proves there is a principal, not that a cookie issued by an
        // older version carries the id this ownership boundary needs.
        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        if (!await ValidateCascadeAsync(
                studiesId, majorId, yearOfStudy, subjectId, cancellationToken))
        {
            return ValidationProblem(ModelState);
        }

        var currentUserId = userId.Value;
        var query = _db.Papers.AsNoTracking();

        // Authorization is the first paper predicate. A later status filter can only
        // narrow this set, never turn a student's request into the global queue.
        if (!User.IsStaff())
        {
            query = query.Where(p =>
                p.Status == PaperStatus.Approved
                || (p.SubmittedByUserId == currentUserId
                    && (p.Status == PaperStatus.Pending
                        || p.Status == PaperStatus.Rejected)));
        }

        if (status is not null)
        {
            query = query.Where(p => p.Status == status.Value);
        }

        if (subjectId is not null)
        {
            // With this applied, filtering on SubjectId then ordering by Year/Month
            // matches the IX_Papers_SubjectId_Year_Month index exactly. The rest of
            // the cascade was already checked; a paper has no major of its own.
            query = query.Where(p => p.SubjectId == subjectId);
        }
        else if (majorId is not null || studiesId is not null || yearOfStudy is not null)
        {
            var inCurriculum = _db.MajorSubjects.AsQueryable();

            if (majorId is not null)
            {
                inCurriculum = inCurriculum.Where(ms => ms.MajorId == majorId);
            }
            else if (studiesId is not null)
            {
                inCurriculum = inCurriculum.Where(ms => ms.Major!.StudiesId == studiesId);
            }

            if (yearOfStudy is not null)
            {
                inCurriculum = inCurriculum.Where(ms => ms.YearOfStudy == yearOfStudy);
            }

            query = query.Where(p => inCurriculum.Select(ms => ms.SubjectId).Contains(p.SubjectId));
        }

        if (examType is not null)
        {
            query = query.Where(p => p.ExamType == examType);
        }

        if (year is not null)
        {
            // Left unbounded, unlike Month: a month outside 1-12 cannot exist and is
            // a client bug, whereas there is no year this archive could not one day
            // hold a paper from.
            query = query.Where(p => p.Year == year);
        }

        if (month is not null)
        {
            query = query.Where(p => p.Month == month);
        }

        // Keep the simple single-status orders when a filter was supplied. Without a
        // status, CASE expressions group the mixed result before each group's own
        // order is applied: pending work first, then rejected, then approved.
        IOrderedQueryable<Paper> ordered;

        if (status == PaperStatus.Pending)
        {
            // A queue is worked oldest-first so nothing rots at the bottom.
            ordered = query
                .OrderBy(p => p.UploadedAt)
                .ThenBy(p => p.Id);
        }
        else if (status is PaperStatus.Rejected or PaperStatus.Approved)
        {
            // Decided papers retain the existing newest-exam-first order.
            ordered = query
                .OrderByDescending(p => p.Year)
                .ThenByDescending(p => p.Month)
                .ThenByDescending(p => p.Id);
        }
        else
        {
            ordered = query
                .OrderBy(p => p.Status == PaperStatus.Pending
                    ? 0
                    : p.Status == PaperStatus.Rejected
                        ? 1
                        : 2)
                .ThenBy(p => p.Status == PaperStatus.Pending
                    ? p.UploadedAt
                    : (DateTime?)null)
                .ThenBy(p => p.Status == PaperStatus.Pending
                    ? p.Id
                    : (int?)null)
                .ThenByDescending(p => p.Status != PaperStatus.Pending
                    ? p.Year
                    : (int?)null)
                .ThenByDescending(p => p.Status != PaperStatus.Pending
                    ? p.Month
                    : (int?)null)
                .ThenByDescending(p => p.Status != PaperStatus.Pending
                    ? p.Id
                    : (int?)null);
        }

        var papers = await ordered
            .Select(p => new PaperDto(
                p.Id,
                p.SubjectId,
                p.Subject!.NameSr,
                p.Subject.NameEn,
                p.ExamType,
                p.Month,
                p.Year,
                p.Files.Count,
                p.UploadedAt,
                p.Status,
                p.ReviewedAt,
                p.RejectionReason,
                p.SubmittedByUserId == currentUserId))
            .ToPagedResultAsync(paging, cancellationToken);

        return Ok(papers);
    }

    // Checked only where two ends of a link were both sent. A prefix of the chain
    // (study with no major, major with no subject) is a filter, not an error.
    private async Task<bool> ValidateCascadeAsync(
        int? studiesId,
        int? majorId,
        int? yearOfStudy,
        int? subjectId,
        CancellationToken cancellationToken)
    {
        if (majorId is not null)
        {
            var majorInfo = await _db.Majors
                .AsNoTracking()
                .Where(m => m.Id == majorId)
                .Select(m => new { m.StudiesId, m.Studies!.YearsOfStudy })
                .FirstOrDefaultAsync(cancellationToken);

            if (studiesId is not null
                && (majorInfo is null || majorInfo.StudiesId != studiesId))
            {
                ModelState.AddModelError(
                    "majorId",
                    $"Major {majorId} does not belong to studies {studiesId}.");

                return false;
            }

            if (yearOfStudy is not null
                && majorInfo is not null
                && yearOfStudy > majorInfo.YearsOfStudy)
            {
                ModelState.AddModelError(
                    "yearOfStudy",
                    $"Year of study {yearOfStudy} is outside the {majorInfo.YearsOfStudy} years of major {majorId}.");

                return false;
            }
        }
        else if (yearOfStudy is not null && studiesId is not null)
        {
            var yearsOfStudy = await _db.Studies
                .AsNoTracking()
                .Where(s => s.Id == studiesId)
                .Select(s => (int?)s.YearsOfStudy)
                .FirstOrDefaultAsync(cancellationToken);

            if (yearsOfStudy is not null && yearOfStudy > yearsOfStudy)
            {
                ModelState.AddModelError(
                    "yearOfStudy",
                    $"Year of study {yearOfStudy} is outside the {yearsOfStudy} years of studies {studiesId}.");

                return false;
            }
        }

        if (subjectId is not null && majorId is not null)
        {
            // One row or none, because (MajorId, SubjectId) is the junction's primary key.
            // The year is fetched rather than compared inside the query so a mismatch can
            // name the year the subject is really taught in.
            var actualYearOfStudy = await _db.MajorSubjects
                .AsNoTracking()
                .Where(ms => ms.MajorId == majorId && ms.SubjectId == subjectId)
                .Select(ms => (int?)ms.YearOfStudy)
                .FirstOrDefaultAsync(cancellationToken);

            if (actualYearOfStudy is null)
            {
                ModelState.AddModelError(
                    "subjectId",
                    $"Subject {subjectId} is not taught in major {majorId}.");

                return false;
            }

            if (yearOfStudy is not null && actualYearOfStudy != yearOfStudy)
            {
                ModelState.AddModelError(
                    "yearOfStudy",
                    $"Subject {subjectId} is taught in year {actualYearOfStudy} of major "
                        + $"{majorId}, not year {yearOfStudy}.");

                return false;
            }

            return true;
        }

        if (subjectId is not null && (studiesId is not null || yearOfStudy is not null))
        {
            var taught = _db.MajorSubjects.AsNoTracking().Where(ms => ms.SubjectId == subjectId);

            if (studiesId is not null)
            {
                taught = taught.Where(ms => ms.Major!.StudiesId == studiesId);
            }

            if (yearOfStudy is not null)
            {
                taught = taught.Where(ms => ms.YearOfStudy == yearOfStudy);
            }

            if (!await taught.AnyAsync(cancellationToken))
            {
                ModelState.AddModelError(
                    "subjectId",
                    studiesId is not null
                        ? $"Subject {subjectId} is not taught in studies {studiesId}."
                        : $"Subject {subjectId} is not taught in year {yearOfStudy}.");

                return false;
            }
        }

        return true;
    }

    // An ordinary account may open its own pending/rejected submission. Another
    // user's unapproved paper is 403: unlike the list endpoint, a direct request
    // names an id and the API explicitly distinguishes forbidden from nonexistent.
    [HttpGet("{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PaperDetailDto>> GetPaper(
        int id,
        CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var paper = await LoadAsync(
            id,
            includeUnapproved: User.IsStaff(),
            ownedByUserId: userId.Value,
            cancellationToken);

        if (paper is not null)
        {
            return Ok(paper);
        }

        // The visibility query deliberately returned no row. Check existence only
        // now, on the exceptional path, so successful detail requests stay one
        // database round trip.
        var exists = await _db.Papers
            .AsNoTracking()
            .AnyAsync(p => p.Id == id, cancellationToken);

        return exists
            ? Problem(
                title: "Paper access denied",
                detail: "You may open approved papers and your own submissions only.",
                statusCode: StatusCodes.Status403Forbidden)
            : NotFound();
    }

    // Staff submissions are published at once; everyone else's wait in the queue. A
    // professor adding a paper is the same authority that would have approved it,
    // whereas an ordinary account proves somebody can be held to a submission, not
    // that the submission is any good.
    //
    // SECURITY: for a staff caller this publishes without review, so the status is
    // derived from the role on the cookie the server itself issued, never from
    // anything in the form.
    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(PaperSubmissionService.MaxTotalUploadBytes)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<UploadedPaperDto>> UploadPaper(
        [FromForm] UploadPaperRequest request,
        CancellationToken cancellationToken)
    {
        var isStaff = User.IsStaff();

        // The id is passed as the nullable it is rather than asserted, because a
        // cookie from an older sign-in could satisfy [Authorize] without carrying
        // the claim.
        var result = await _submissions.SubmitAsync(
            request,
            isStaff ? PaperStatus.Approved : PaperStatus.Pending,
            User.GetUserId(),
            cancellationToken);

        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(error.Field, error.Message);
            }

            return ValidationProblem(ModelState);
        }

        // The submitter can open their own pending paper, so Location is valid for
        // every successful upload — not only a staff one that is already public.
        return CreatedAtAction(
            nameof(GetPaper),
            new { id = result.Paper!.Id },
            UploadedPaperDto.From(result.Paper));
    }

    // Also reverses a rejection, which clears the stored reason. Approving a paper
    // that is already approved changes nothing and still returns 200, so a
    // double-clicked button is not an error.
    [HttpPost("{id:int}/approve")]
    [Authorize(Policy = RolePolicies.Staff)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PaperDetailDto>> ApprovePaper(
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

        return Ok(await LoadAsync(
            id, includeUnapproved: true, ownedByUserId: null, cancellationToken));
    }

    // The files stay on disk: a rejection is a judgement that can be revisited, and
    // deleting on rejection would make that unrecoverable. Rejecting an
    // already-rejected paper updates the reason.
    [HttpPost("{id:int}/reject")]
    [Authorize(Policy = RolePolicies.Staff)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PaperDetailDto>> RejectPaper(
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

        return Ok(await LoadAsync(
            id, includeUnapproved: true, ownedByUserId: null, cancellationToken));
    }

    // Staff rather than administrator: this is a judgement about one paper, which is
    // what a moderator is for. The subject catalogue it files against is the admin's.
    [HttpPut("{id:int}")]
    [Authorize(Policy = RolePolicies.Staff)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PaperDetailDto>> UpdatePaper(
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

        return Ok(await LoadAsync(
            id, includeUnapproved: true, ownedByUserId: null, cancellationToken));
    }

    // Distinct from rejection, which stays reversible. This is for papers that should
    // not exist at all, and it is not reversible — which is why it is the one action
    // here restricted to an administrator.
    [HttpDelete("{id:int}")]
    [Authorize(Policy = RolePolicies.Administrators)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
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
        // to save would leave rows pointing at nothing; this order can at worst leave
        // bytes with no row, which is only wasted disk.
        _storage.TryDeleteOrphans(storedPaths, _logger);

        _logger.LogInformation(
            "{Admin} deleted paper {PaperId} and its {FileCount} file(s).",
            User.Identity?.Name,
            id,
            storedPaths.Count);

        return NoContent();
    }

    // Visibility inputs are passed rather than read from User inside: staff action
    // responses include every status, while a normal detail request may include an
    // unapproved paper only when ownedByUserId matches its submitter.
    //
    // The pages are projected in the same query as the paper: a separate round trip
    // per paper would be the classic N+1 in disguise. Grouping happens after the
    // query, being only a rearrangement of rows already fetched.
    private async Task<PaperDetailDto?> LoadAsync(
        int id,
        bool includeUnapproved,
        int? ownedByUserId,
        CancellationToken cancellationToken)
    {
        var paper = await _db.Papers
            .AsNoTracking()
            .Where(p => p.Id == id)
            .Where(p =>
                includeUnapproved
                || p.Status == PaperStatus.Approved
                || (ownedByUserId != null && p.SubmittedByUserId == ownedByUserId))
            .Select(p => new
            {
                p.Id,
                p.SubjectId,
                SubjectNameSr = p.Subject!.NameSr,
                SubjectNameEn = p.Subject.NameEn,
                p.ExamType,
                p.Month,
                p.Year,
                PageCount = p.Files.Count,
                p.UploadedAt,
                p.Status,
                p.ReviewedAt,
                p.RejectionReason,
                Files = p.Files
                    .OrderBy(f => f.PageNumber)
                    .Select(f => new PaperFileDto(f.PageNumber, f.ContentType, f.SizeBytes))
                    .ToList()
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (paper is null)
        {
            return null;
        }

        return new PaperDetailDto(
            paper.Id,
            paper.SubjectId,
            paper.SubjectNameSr,
            paper.SubjectNameEn,
            paper.ExamType,
            paper.Month,
            paper.Year,
            paper.PageCount,
            paper.UploadedAt,
            paper.Status,
            paper.ReviewedAt,
            paper.RejectionReason,
            new PaperFilesDto(paper.Files));
    }
}
