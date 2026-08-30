using System.ComponentModel.DataAnnotations;
using ExamArchive.Data;
using ExamArchive.Dtos;
using ExamArchive.Models;
using ExamArchive.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ExamArchive.Controllers;

/// <summary>
/// Everything the archive does with papers: browsing, submitting, checking on a
/// submission, and — for staff — reviewing and deciding on what has been submitted.
/// </summary>
/// <remarks>
/// One controller for one resource. A paper is a paper whoever is asking; what
/// changes with the caller is which papers they may see and what they may do to
/// them, and both of those are now decided per action by the role on the session
/// cookie rather than by which URL was used to arrive.
/// <para>
/// The cost of merging is worth stating plainly, because it was the reason these
/// were once two types. A controller serving both audiences cannot default to
/// "staff only" — most of it is public — so the class-level attribute is the weaker
/// <c>[Authorize]</c>, and an endpoint added here without an explicit policy is
/// reachable by any signed-in account. That is no longer a hole nothing reports,
/// but it is no longer a hole nothing can open either: every staff action below
/// names its policy, and a new one that forgets to is wrong in a way that has to be
/// caught by reading it. In exchange there is one set of routes, one DTO, and no
/// second copy of the archive to keep in step.
/// </para>
/// <para>
/// Visibility follows the same principle. <see cref="ClaimsPrincipalExtensions.IsStaff"/>
/// decides whether unapproved papers exist as far as this caller is concerned, and
/// it is passed explicitly at each call site rather than consulted deep inside a
/// query, so the rule stays something you can read off the action.
/// </para>
/// </remarks>
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

    /// <summary>
    /// Lists papers: the approved archive for everyone, or a review queue for staff.
    /// </summary>
    /// <param name="studiesId">
    /// The level of study the searcher started from. Checked, not filtered on —
    /// see the remarks.
    /// </param>
    /// <param name="majorId">The major they narrowed to. Checked, not filtered on.</param>
    /// <param name="yearOfStudy">
    /// The year of the curriculum they narrowed to. Checked, not filtered on.
    /// Requires <paramref name="majorId"/>, since a year of study is a property of
    /// the major/subject pairing and means nothing without one.
    /// </param>
    /// <param name="subjectId">
    /// Restricts the list to one subject. Optional: omitted, the whole archive is
    /// browsed, which is what a landing page showing recent additions needs.
    /// </param>
    /// <param name="examType">Restricts the list to one kind of sitting.</param>
    /// <param name="month">The month the exam was held, 1-12.</param>
    /// <param name="year">The calendar year the exam was held — not the year of study.</param>
    /// <param name="paging">Which page to return. See <see cref="PageRequest"/>.</param>
    /// <param name="status">
    /// Which papers to list. Approved by default, which is the only value a
    /// non-staff caller may ask for; Pending and Rejected are the review queues.
    /// </param>
    /// <remarks>
    /// The one endpoint that used to be two, and the merge is visible here more than
    /// anywhere else: browsing the archive and working the queue are the same query
    /// over the same table, differing in which status is wanted.
    /// <para>
    /// Ordering follows from that choice rather than from the caller. A queue is
    /// worked oldest-first, so the submission that has been waiting longest is the
    /// next one dealt with and nothing rots at the bottom of the list. The archive is
    /// browsed newest-exam-first, because somebody looking for last year's final does
    /// not want to start in 1994.
    /// </para>
    /// <para>
    /// The filter parameters do three different jobs, which is worth being explicit
    /// about because only one of them changes the rows that come back.
    /// </para>
    /// <para>
    /// <paramref name="subjectId"/> filters. A paper belongs to a subject and to
    /// nothing else, so once the subject is known the archive is as narrow as this
    /// hierarchy can make it.
    /// </para>
    /// <para>
    /// <paramref name="studiesId"/>, <paramref name="majorId"/> and
    /// <paramref name="yearOfStudy"/> narrow nothing — they are the path the
    /// searcher walked to arrive at the subject. They are here because that path
    /// cannot be recovered from the subject: the same subject is taught in several
    /// majors, in different years, so <c>subjectId=5</c> alone cannot tell a
    /// reloaded page which of them was picked, and its dropdowns and breadcrumb have
    /// nothing to restore themselves from. Carrying them makes a search URL a
    /// complete description of what was asked, which is what makes it shareable and
    /// bookmarkable.
    /// </para>
    /// <para>
    /// Being sent and not used is exactly why they are verified rather than ignored.
    /// A path the server has agreed is consistent is one a client can safely draw a
    /// breadcrumb from; an unverified one it merely echoes back is worth nothing,
    /// and after a curriculum change it would have the page confidently displaying
    /// a year the subject is no longer taught in. So a contradiction is a 400 — see
    /// <see cref="ValidateCascadeAsync"/> — at the cost of a stale bookmark failing
    /// loudly instead of quietly misinforming.
    /// </para>
    /// <para>
    /// <paramref name="examType"/>, <paramref name="month"/> and
    /// <paramref name="year"/> filter, and are the optional part: a searcher who
    /// already has the subject uses them to cut a long list down, and omitting them
    /// is not an incomplete search.
    /// </para>
    /// </remarks>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PagedResult<PaperDto>>> GetPapers(
        [FromQuery, Range(1, int.MaxValue, ErrorMessage = "StudiesId must be a positive id.")]
        int? studiesId,
        [FromQuery, Range(1, int.MaxValue, ErrorMessage = "MajorId must be a positive id.")]
        int? majorId,
        [FromQuery, Range(1, 6, ErrorMessage = "YearOfStudy must be between 1 and 6.")]
        int? yearOfStudy,
        [FromQuery, Range(1, int.MaxValue, ErrorMessage = "SubjectId must be a positive id.")]
        int? subjectId,
        [FromQuery] ExamType? examType,
        [FromQuery, Range(1, 12, ErrorMessage = "Month must be between 1 and 12.")]
        int? month,
        [FromQuery] int? year,
        [FromQuery] PageRequest paging,
        [FromQuery] PaperStatus status = PaperStatus.Approved,
        CancellationToken cancellationToken = default)
    {
        // 401 rather than 403, and rather than quietly forcing the value back to
        // Approved. Silently answering a different question than the one asked is
        // the failure this codebase avoids elsewhere too — see ValidateCascadeAsync
        // — and the code matches the one the role gates use, so a client cannot
        // learn from the status whether a queue exists to be refused.
        if (status != PaperStatus.Approved && !User.IsStaff())
        {
            return Unauthorized();
        }

        if (!await ValidateCascadeAsync(
                studiesId, majorId, yearOfStudy, subjectId, cancellationToken))
        {
            return ValidationProblem(ModelState);
        }

        var query = _db.Papers
            .AsNoTracking()
            .Where(p => p.Status == status);

        if (subjectId is not null)
        {
            // With this applied, filtering on SubjectId then ordering by Year/Month
            // matches the IX_Papers_SubjectId_Year_Month index exactly.
            query = query.Where(p => p.SubjectId == subjectId);
        }

        if (examType is not null)
        {
            query = query.Where(p => p.ExamType == examType);
        }

        // Year and Month are the index's second and third columns, so a search that
        // has already fixed the subject narrows further along the same index rather
        // than filtering the rows it returns.
        if (year is not null)
        {
            // Left unbounded, unlike Month. A month outside 1-12 cannot exist and is
            // a client bug worth reporting, whereas there is no year this archive
            // could not one day hold a paper from — so an unlikely year matches
            // nothing and says so, rather than being refused on a guess.
            query = query.Where(p => p.Year == year);
        }

        if (month is not null)
        {
            query = query.Where(p => p.Month == month);
        }

        var ordered = status == PaperStatus.Pending

            // Backed by IX_Papers_SubmittedByUserId's sibling on UploadedAt; Id last
            // for the same total-order reason as below.
            ? query.OrderBy(p => p.UploadedAt).ThenBy(p => p.Id)
            : query
                .OrderByDescending(p => p.Year)
                .ThenByDescending(p => p.Month)

                // Id last so the order is total, which paging depends on: without it
                // two papers sat in the same month are tie-broken however the server
                // feels on the day, and a row can appear on two pages or on none.
                .ThenByDescending(p => p.Id);

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
                p.RejectionReason))
            .ToPagedResultAsync(paging, cancellationToken);

        return Ok(papers);
    }

    /// <summary>
    /// Checks that the path a searcher took to a subject actually exists, adding a
    /// model error and returning false if any link in it is broken.
    /// </summary>
    /// <remarks>
    /// Verified link by link — studies to major, then major to subject — because
    /// that is how the chain is built and how a client walks it. A link whose upper
    /// end was not sent cannot be checked and is left alone: <c>studiesId</c> with a
    /// subject but no major describes a route through a major nobody named, and
    /// there is no row that would confirm or deny it. That combination is not one
    /// the search form produces.
    /// <para>
    /// At most one query runs, and only when something above the subject was sent —
    /// the plain <c>?subjectId=5</c> browse and the unfiltered landing page both
    /// leave here without touching the database.
    /// </para>
    /// </remarks>
    private async Task<bool> ValidateCascadeAsync(
        int? studiesId,
        int? majorId,
        int? yearOfStudy,
        int? subjectId,
        CancellationToken cancellationToken)
    {
        if (studiesId is null && majorId is null && yearOfStudy is null)
        {
            return true;
        }

        // These three describe how a subject was arrived at, so without a subject
        // there is nothing for them to describe. Refused rather than quietly
        // dropped: a caller sending majorId alone means to narrow the archive to
        // that major, and handing back every paper in the archive would answer a
        // question they did not ask while looking like it had worked.
        if (subjectId is null)
        {
            ModelState.AddModelError(
                "subjectId",
                "SubjectId is required when studiesId, majorId or yearOfStudy is given.");

            return false;
        }

        // Year of study lives on the major/subject pairing — the same subject sits
        // in year 2 of one major and year 3 of another — so without a major it
        // identifies no row and cannot be checked against one.
        if (yearOfStudy is not null && majorId is null)
        {
            ModelState.AddModelError(
                "majorId",
                "MajorId is required when yearOfStudy is given.");

            return false;
        }

        if (studiesId is not null && majorId is not null)
        {
            var belongs = await _db.Majors
                .AsNoTracking()
                .AnyAsync(
                    m => m.Id == majorId && m.StudiesId == studiesId,
                    cancellationToken);

            if (!belongs)
            {
                ModelState.AddModelError(
                    "majorId",
                    $"Major {majorId} does not belong to studies {studiesId}.");

                return false;
            }
        }

        if (majorId is null)
        {
            return true;
        }

        // One row or none, because (MajorId, SubjectId) is the junction's primary
        // key. The year is fetched rather than compared inside the query so that a
        // mismatch can name the year the subject is really taught in — enough for a
        // client to repair a stale bookmark instead of only reporting it broken.
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

    /// <summary>
    /// Fetches one paper and its pages. Approved papers are public; staff see any
    /// status.
    /// </summary>
    /// <remarks>
    /// A detail page needs this: reaching a paper by its own URL is otherwise
    /// impossible without listing its whole subject and searching the result. A
    /// moderator opening a submission from the queue lands on the same route.
    /// <para>
    /// The pages come back with the paper rather than from a second request. What a
    /// client did with that second request — count the pages, and decide which viewer
    /// each one needs — it can now do from this response, and the formats arrive
    /// already sorted into their groups. See <see cref="PaperFilesDto"/>.
    /// </para>
    /// <para>
    /// For a caller who is not staff, an unapproved paper is 404 rather than 403.
    /// Telling the two apart would let anyone probe ids to learn that a pending paper
    /// exists, which is the thing hiding it prevents in the first place.
    /// </para>
    /// </remarks>
    [HttpGet("{id:int}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PaperDetailDto>> GetPaper(
        int id,
        CancellationToken cancellationToken)
    {
        var paper = await LoadAsync(id, User.IsStaff(), cancellationToken);

        return paper is null ? NotFound() : Ok(paper);
    }

    /// <summary>
    /// Submits an exam paper. Staff submissions are published at once; everyone
    /// else's wait in the queue with status "Pending".
    /// </summary>
    /// <remarks>
    /// Requires an account, which is a change from the archive's original design.
    /// Anonymity was worth something to a submitter who would rather not be named
    /// as the source of a circulating exam paper, and it is given up because an
    /// endpoint open to everyone accepts spam and deliberately misleading files at
    /// the same rate it accepts papers, and a moderator sorting those by hand is a
    /// worse outcome than a sign-in prompt.
    /// <para>
    /// The queue is skipped for staff and for nobody else. A professor or assistant
    /// adding a paper is the same authority that would have approved it, so making
    /// them upload it and then approve their own submission is a step that decides
    /// nothing. An ordinary account proves somebody can be held to a submission, not
    /// that the submission is any good, so it still waits for a decision.
    /// </para>
    /// <para>
    /// SECURITY: for a staff caller this publishes to the archive without review,
    /// which makes it the most valuable thing here to an attacker. The status is
    /// derived from the role on the cookie the server itself issued, never from
    /// anything in the form.
    /// </para>
    /// </remarks>
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

        // [Authorize] on the class has already established there is a principal, so
        // the id is present — but it is passed as the nullable it is rather than
        // asserted, because a cookie from an older sign-in could satisfy the
        // attribute without carrying the claim.
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

        if (!isStaff)
        {
            // 201 without a Location header: a pending paper has no address yet. It
            // is deliberately absent from the approved listing, and GET on its id
            // would 404 for the very account that just created it.
            return StatusCode(
                StatusCodes.Status201Created,
                UploadedPaperDto.From(result.Paper!, result.ClaimToken));
        }

        // A staff upload does have somewhere to point: it is approved, so it is
        // already reachable. No claim code — they can see it in the archive.
        return CreatedAtAction(
            nameof(GetPaper),
            new { id = result.Paper!.Id },
            UploadedPaperDto.From(result.Paper));
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
    /// Pending and rejected papers appear here for callers who may not list them
    /// anywhere else. That is not a leak of the review queue: the filter is the
    /// caller's own id, so this shows a submitter their own work and no one else's.
    /// </para>
    /// </remarks>
    [HttpGet("mine")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PagedResult<SubmissionStatusDto>>> GetMySubmissions(
        [FromQuery] PageRequest paging,
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
            .ToPagedResultAsync(paging, cancellationToken);

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
    /// A rejection reason is written for the submitter, and without somewhere to
    /// read it the field is only ever seen by the moderator who typed it — the
    /// submitter learns nothing and uploads the same unreadable photo again. This is
    /// the route for a submitter who has the code but is not signed in on the device
    /// they are asking from; <see cref="GetMySubmissions"/> is the one for an account.
    /// <para>
    /// The code is the only credential, so anyone holding it sees this. That is
    /// acceptable because of how little it reveals: a subject, a date, and a note
    /// about photo quality, on a paper the holder of the code almost certainly
    /// submitted. It is not a way into the archive — it describes a submission and
    /// hands over nothing from it, and <see cref="GetPaper"/> still answers 404 for
    /// an unapproved paper unless the caller is staff.
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

    /// <summary>
    /// Publishes a paper: it becomes visible to everyone, signed in or not.
    /// </summary>
    /// <remarks>
    /// Also reverses a rejection, which clears the stored reason — leaving it
    /// behind would put a rejection note on a published paper. Approving a paper
    /// that is already approved changes nothing and still returns 200, so a
    /// double-clicked button is not an error.
    /// </remarks>
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

        return Ok(await LoadAsync(id, includeUnapproved: true, cancellationToken));
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

        return Ok(await LoadAsync(id, includeUnapproved: true, cancellationToken));
    }

    /// <summary>
    /// Corrects a paper's filing details: its subject, sitting, month and year.
    /// </summary>
    /// <remarks>
    /// A paper filed under the wrong subject or year is not wrong enough to reject —
    /// the scan is fine and the submitter did nothing wrong — but with no way to edit
    /// it, the only remedies were to leave it misfiled or to reject and re-upload it,
    /// and neither is honest.
    /// <para>
    /// Staff rather than administrator. This is a judgement about one paper, which is
    /// exactly what a moderator is for; the subject catalogue it files against is the
    /// admin's.
    /// </para>
    /// </remarks>
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

        return Ok(await LoadAsync(id, includeUnapproved: true, cancellationToken));
    }

    /// <summary>
    /// Removes a paper from the archive entirely, along with its files.
    /// </summary>
    /// <remarks>
    /// Distinct from rejection, which is a decision about whether to publish and
    /// stays reversible. This is for papers that should not exist at all —
    /// duplicates, a submission somebody asks to have withdrawn, a scan carrying
    /// something personal — and it is not reversible, which is why it is the one
    /// action here restricted to an administrator rather than to staff generally.
    /// <para>
    /// The PaperFiles rows go with it through the cascade configured on the foreign
    /// key. The bytes on disk do not: cascade deletes rows, and a paper deleted
    /// without this call would leak its files silently.
    /// </para>
    /// </remarks>
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
    /// Reads one paper, with its pages, in the shape every single-paper endpoint
    /// here returns it in.
    /// </summary>
    /// <param name="includeUnapproved">
    /// True when the caller may see papers the public cannot. Passed rather than
    /// read from <c>User</c> inside, so that the decisions after a moderator acts —
    /// where the answer is always true — do not depend on re-deriving it.
    /// </param>
    /// <remarks>
    /// The pages are projected in the same query as the paper. A separate round trip
    /// per paper would be the classic N+1 in disguise, and there is nothing to gain
    /// from it: a paper has at most
    /// <see cref="UploadPaperRequest.MaxFiles"/> of them.
    /// <para>
    /// Grouping happens after the query rather than inside it. It is a rearrangement
    /// of rows already fetched, and expressing it in LINQ that MySQL can translate
    /// would cost more than it saves.
    /// </para>
    /// </remarks>
    private async Task<PaperDetailDto?> LoadAsync(
        int id,
        bool includeUnapproved,
        CancellationToken cancellationToken)
    {
        var paper = await _db.Papers
            .AsNoTracking()
            .Where(p => p.Id == id)
            .Where(p => includeUnapproved || p.Status == PaperStatus.Approved)
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
