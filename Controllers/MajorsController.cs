using ExamArchive.Data;
using ExamArchive.Dtos;
using ExamArchive.Models;
using ExamArchive.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ExamArchive.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
[Authorize]
public class MajorsController : ControllerBase
{
    private readonly ExamArchiveDbContext _db;
    private readonly ILogger<MajorsController> _logger;

    public MajorsController(ExamArchiveDbContext db, ILogger<MajorsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PagedResult<MajorDto>>> GetMajors(
        [FromQuery] int? studiesId,
        [FromQuery] PageRequest paging,
        CancellationToken cancellationToken)
    {
        var query = _db.Majors.AsNoTracking();

        if (studiesId is not null)
        {
            query = query.Where(m => m.StudiesId == studiesId);
        }

        // Not sorted by name: the correct order depends on which language and script
        // the reader picked, which only the client knows. Id is unique, so ordering
        // by it alone is already the total order paging needs.
        var majors = await query
            .OrderBy(m => m.Id)
            .Select(m => new MajorDto(m.Id, m.NameSr, m.NameEn, m.StudiesId))
            .ToPagedResultAsync(paging, cancellationToken);

        return Ok(majors);
    }

    [HttpPost]
    [Authorize(Policy = RolePolicies.Administrators)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<MajorDto>> CreateMajor(
        [FromBody] SaveMajorRequest request,
        CancellationToken cancellationToken)
    {
        if (!await StudiesExistsAsync(request.StudiesId, cancellationToken))
        {
            return UnknownStudies(request.StudiesId);
        }

        var major = new Major
        {
            NameSr = request.NameSr.Trim(),
            NameEn = OptionalName(request.NameEn),
            StudiesId = request.StudiesId
        };

        _db.Majors.Add(major);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "{Admin} created major {MajorId} under studies {StudiesId}.",
            User.Identity?.Name,
            major.Id,
            major.StudiesId);

        return CreatedAtAction(nameof(GetMajors), new { studiesId = major.StudiesId }, ToDto(major));
    }

    [HttpPut("{id:int}")]
    [Authorize(Policy = RolePolicies.Administrators)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MajorDto>> UpdateMajor(
        int id,
        [FromBody] SaveMajorRequest request,
        CancellationToken cancellationToken)
    {
        var major = await _db.Majors.FirstOrDefaultAsync(m => m.Id == id, cancellationToken);

        if (major is null)
        {
            return NotFound();
        }

        var targetStudy = await _db.Studies
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == request.StudiesId, cancellationToken);

        if (targetStudy is null)
        {
            return UnknownStudies(request.StudiesId);
        }

        var occupiedYear = await _db.MajorSubjects
            .Where(ms => ms.MajorId == id)
            .Select(ms => (int?)ms.YearOfStudy)
            .MaxAsync(cancellationToken);

        if (occupiedYear is not null && occupiedYear > targetStudy.YearsOfStudy)
        {
            return Problem(
                title: "Years of study in use",
                detail: $"Cannot move major {id} to studies {request.StudiesId}, which has {targetStudy.YearsOfStudy} years, while a subject is taught in year {occupiedYear}.",
                statusCode: StatusCodes.Status409Conflict);
        }

        major.NameSr = request.NameSr.Trim();
        major.NameEn = OptionalName(request.NameEn);
        major.StudiesId = request.StudiesId;

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("{Admin} updated major {MajorId}.", User.Identity?.Name, id);

        return Ok(ToDto(major));
    }

    [HttpDelete("{id:int}")]
    [Authorize(Policy = RolePolicies.Administrators)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteMajor(int id, CancellationToken cancellationToken)
    {
        var major = await _db.Majors.FirstOrDefaultAsync(m => m.Id == id, cancellationToken);

        if (major is null)
        {
            return NotFound();
        }

        // Papers hang off Subject, not Major. Deleting this major only unlinks the
        // curriculum; it is refused when that would hide papers that have no other
        // major left to appear under.
        var hidesPapers = await _db.MajorSubjects
            .Where(ms => ms.MajorId == id)
            .Where(ms => ms.Subject!.Papers.Any())
            .Where(ms => !_db.MajorSubjects.Any(
                other => other.SubjectId == ms.SubjectId && other.MajorId != id))
            .AnyAsync(cancellationToken);

        if (hidesPapers)
        {
            return Problem(
                title: "Major in use",
                detail: $"Major {id} is the only major teaching a subject that still has papers.",
                statusCode: StatusCodes.Status409Conflict);
        }

        _db.Majors.Remove(major);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("{Admin} deleted major {MajorId}.", User.Identity?.Name, id);

        return NoContent();
    }

    // Places an existing subject into this major's curriculum. Creating the subject
    // itself is POST /api/subjects; this is how the same course appears in a second
    // set. Unlinking without deleting either side is a later endpoint.
    [HttpPost("{id:int}/subjects")]
    [Authorize(Policy = RolePolicies.Administrators)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SubjectDto>> AttachSubject(
        int id,
        [FromBody] AttachSubjectRequest request,
        CancellationToken cancellationToken)
    {
        var yearsOfStudy = await _db.Majors
            .AsNoTracking()
            .Where(m => m.Id == id)
            .Select(m => (int?)m.Studies!.YearsOfStudy)
            .FirstOrDefaultAsync(cancellationToken);

        if (yearsOfStudy is null)
        {
            return NotFound();
        }

        if (request.YearOfStudy > yearsOfStudy)
        {
            ModelState.AddModelError(
                nameof(request.YearOfStudy),
                $"Year of study {request.YearOfStudy} is outside the {yearsOfStudy} years of major {id}.");

            return ValidationProblem(ModelState);
        }

        var subject = await _db.Subjects
            .FirstOrDefaultAsync(s => s.Id == request.SubjectId, cancellationToken);

        if (subject is null)
        {
            ModelState.AddModelError(
                nameof(request.SubjectId),
                $"No subject with id {request.SubjectId} exists.");

            return ValidationProblem(ModelState);
        }

        var alreadyLinked = await _db.MajorSubjects.AnyAsync(
            ms => ms.MajorId == id && ms.SubjectId == request.SubjectId,
            cancellationToken);

        if (alreadyLinked)
        {
            return Problem(
                title: "Subject already in major",
                detail: $"Subject {request.SubjectId} is already taught in major {id}.",
                statusCode: StatusCodes.Status409Conflict);
        }

        _db.MajorSubjects.Add(new MajorSubject
        {
            MajorId = id,
            SubjectId = request.SubjectId,
            YearOfStudy = request.YearOfStudy
        });

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "{Admin} added subject {SubjectId} to major {MajorId} in year {Year}.",
            User.Identity?.Name,
            request.SubjectId,
            id,
            request.YearOfStudy);

        var dto = new SubjectDto(
            subject.Id,
            subject.Code,
            subject.NameSr,
            subject.NameEn,
            request.YearOfStudy);

        return CreatedAtAction(
            nameof(SubjectsController.GetSubjects),
            "Subjects",
            new { majorId = id },
            dto);
    }

    private Task<bool> StudiesExistsAsync(int studiesId, CancellationToken cancellationToken) =>
        _db.Studies.AnyAsync(s => s.Id == studiesId, cancellationToken);

    private ActionResult UnknownStudies(int studiesId)
    {
        ModelState.AddModelError(
            nameof(SaveMajorRequest.StudiesId),
            $"No studies with id {studiesId} exists.");

        return ValidationProblem(ModelState);
    }

    private static MajorDto ToDto(Major major) =>
        new(major.Id, major.NameSr, major.NameEn, major.StudiesId);

    private static string? OptionalName(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
