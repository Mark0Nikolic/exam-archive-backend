using ExamArchive.Data;
using ExamArchive.Dtos;
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

    public StudiesController(ExamArchiveDbContext db)
    {
        _db = db;
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
}
