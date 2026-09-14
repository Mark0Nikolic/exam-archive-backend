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
public class MajorsController : ControllerBase
{
    private readonly ExamArchiveDbContext _db;

    public MajorsController(ExamArchiveDbContext db)
    {
        _db = db;
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
}
