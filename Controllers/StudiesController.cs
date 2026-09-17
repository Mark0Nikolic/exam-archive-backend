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
public class StudiesController : ControllerBase
{
    private readonly ExamArchiveDbContext _db;
    private readonly ILogger<StudiesController> _logger;

    public StudiesController(ExamArchiveDbContext db, ILogger<StudiesController> logger)
    {
        _db = db;
        _logger = logger;
    }

    // Takes no filter: there is nothing above a level of study to narrow it by, and
    // this is where the browse chain has to start.
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PagedResult<StudiesDto>>> GetStudies(
        [FromQuery] PageRequest paging,
        CancellationToken cancellationToken)
    {
        // Ordered by Id, which is both the total order paging needs and the order the
        // levels were seeded in — bachelor's before master's. Not sorted by name, for
        // the same reason as MajorsController. YearsOfStudy is the year picker: the
        // client renders 1..N rather than a hardcoded length.
        var studies = await _db.Studies
            .AsNoTracking()
            .OrderBy(s => s.Id)
            .Select(s => new StudiesDto(s.Id, s.NameSr, s.NameEn, s.YearsOfStudy))
            .ToPagedResultAsync(paging, cancellationToken);

        return Ok(studies);
    }

    [HttpPost]
    [Authorize(Policy = RolePolicies.Administrators)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<StudiesDto>> CreateStudies(
        [FromBody] SaveStudiesRequest request,
        CancellationToken cancellationToken)
    {
        var studies = new Studies
        {
            NameSr = request.NameSr.Trim(),
            NameEn = OptionalName(request.NameEn),
            YearsOfStudy = request.YearsOfStudy
        };

        _db.Studies.Add(studies);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "{Admin} created studies {StudiesId} ({Name}).",
            User.Identity?.Name,
            studies.Id,
            studies.NameSr);

        return CreatedAtAction(nameof(GetStudies), ToDto(studies));
    }

    [HttpPut("{id:int}")]
    [Authorize(Policy = RolePolicies.Administrators)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StudiesDto>> UpdateStudies(
        int id,
        [FromBody] SaveStudiesRequest request,
        CancellationToken cancellationToken)
    {
        var studies = await _db.Studies.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

        if (studies is null)
        {
            return NotFound();
        }

        var occupiedYear = await _db.MajorSubjects
            .Where(ms => ms.Major!.StudiesId == id)
            .Select(ms => (int?)ms.YearOfStudy)
            .MaxAsync(cancellationToken);

        if (occupiedYear is not null && request.YearsOfStudy < occupiedYear)
        {
            return Problem(
                title: "Years of study in use",
                detail: $"Cannot reduce to {request.YearsOfStudy} years while a subject is taught in year {occupiedYear}.",
                statusCode: StatusCodes.Status409Conflict);
        }

        studies.NameSr = request.NameSr.Trim();
        studies.NameEn = OptionalName(request.NameEn);
        studies.YearsOfStudy = request.YearsOfStudy;

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("{Admin} updated studies {StudiesId}.", User.Identity?.Name, id);

        return Ok(ToDto(studies));
    }

    [HttpDelete("{id:int}")]
    [Authorize(Policy = RolePolicies.Administrators)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteStudies(int id, CancellationToken cancellationToken)
    {
        var studies = await _db.Studies.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

        if (studies is null)
        {
            return NotFound();
        }

        var hasMajors = await _db.Majors.AnyAsync(m => m.StudiesId == id, cancellationToken);

        if (hasMajors)
        {
            return Problem(
                title: "Studies in use",
                detail: $"Studies {id} still has majors. Delete those first.",
                statusCode: StatusCodes.Status409Conflict);
        }

        _db.Studies.Remove(studies);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("{Admin} deleted studies {StudiesId}.", User.Identity?.Name, id);

        return NoContent();
    }

    private static StudiesDto ToDto(Studies studies) =>
        new(studies.Id, studies.NameSr, studies.NameEn, studies.YearsOfStudy);

    private static string? OptionalName(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
