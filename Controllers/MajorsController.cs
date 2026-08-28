using ExamArchive.Data;
using ExamArchive.Dtos;
using ExamArchive.Services;
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
    /// <param name="paging">Which page to return. See <see cref="PageRequest"/>.</param>
    /// <remarks>
    /// Paged like every other listing, though a faculty has a few dozen majors and
    /// this one could have returned them all. The point is that a client parses one
    /// response shape everywhere rather than remembering which endpoints wrap their
    /// rows — and a caller filling a dropdown can still ask for
    /// <c>perPage=100</c> and be done in one request.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
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

        // Not sorted by name here: the correct order depends on which language and
        // script the reader picked, which only the client knows — so it sorts, with
        // localeCompare(_, 'sr').
        //
        // Projecting in the query means EF selects only these columns and never
        // materialises an entity graph, so there is nothing to serialise circularly.
        //
        // Id is unique, so ordering by it alone is already a total order and needs
        // no tiebreaker to page safely.
        var majors = await query
            .OrderBy(m => m.Id)
            .Select(m => new MajorDto(m.Id, m.NameSr, m.NameEn, m.StudiesId))
            .ToPagedResultAsync(paging, cancellationToken);

        return Ok(majors);
    }
}
