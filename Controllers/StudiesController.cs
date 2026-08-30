using ExamArchive.Data;
using ExamArchive.Dtos;
using ExamArchive.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ExamArchive.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class StudiesController : ControllerBase
{
    private readonly ExamArchiveDbContext _db;

    public StudiesController(ExamArchiveDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Lists the levels of study — the top of the hierarchy, and the first choice
    /// a searcher makes.
    /// </summary>
    /// <param name="paging">Which page to return. See <see cref="PageRequest"/>.</param>
    /// <remarks>
    /// Takes no filter, because there is nothing above a level of study to narrow
    /// it by. Every other listing in the browse API is reached by holding an id
    /// from the listing before it, and this is where that chain has to start: until
    /// it existed a client could filter majors by <c>studiesId</c> but had no way to
    /// learn which ids there were.
    /// <para>
    /// Two rows today, seeded by migration, so paging this is very nearly ceremony.
    /// It is here anyway for the reason given on <see cref="PagedResult{T}"/> — a
    /// client that can assume one response shape for every listing writes the
    /// unwrapping once.
    /// </para>
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<StudiesDto>>> GetStudies(
        [FromQuery] PageRequest paging,
        CancellationToken cancellationToken)
    {
        // Ordered by Id, which is both a total order — what paging needs, see
        // PagingExtensions — and the order the levels were seeded in, bachelor's
        // before master's, which is the order a reader expects to be offered them.
        //
        // Not sorted by name, for the same reason as MajorsController: the right
        // order depends on which language and script the reader picked, and only
        // the client knows that.
        var studies = await _db.Studies
            .AsNoTracking()
            .OrderBy(s => s.Id)
            .Select(s => new StudiesDto(s.Id, s.NameSr, s.NameEn))
            .ToPagedResultAsync(paging, cancellationToken);

        return Ok(studies);
    }
}
