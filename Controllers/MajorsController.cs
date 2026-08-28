using ExamArchive.Data;
using ExamArchive.Dtos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ExamArchive.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class MajorsController : ControllerBase
{
    private readonly ExamArchiveDbContext _db;

    public MajorsController(ExamArchiveDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Lists majors, optionally narrowed to a single study level.
    /// </summary>
    /// <param name="studiesId">Study level to filter by. Omit to list every major.</param>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<MajorDto>>> GetMajors(
        [FromQuery] int? studiesId,
        CancellationToken cancellationToken)
    {
        var query = _db.Majors.AsNoTracking();

        if (studiesId is not null)
        {
            query = query.Where(m => m.StudiesId == studiesId);
        }

        // Not sorted by name here: the correct order depends on which language and
        // script the reader picked, which only the client knows — so it sorts, with
        // localeCompare(_, 'sr').
        //
        // Projecting in the query means EF selects only these columns and never
        // materialises an entity graph, so there is nothing to serialise circularly.
        var majors = await query
            .OrderBy(m => m.Id)
            .Select(m => new MajorDto(m.Id, m.NameSr, m.NameEn, m.StudiesId))
            .ToListAsync(cancellationToken);

        return Ok(majors);
    }
}
