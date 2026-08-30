using System.ComponentModel.DataAnnotations;
using ExamArchive.Data;
using ExamArchive.Dtos;
using ExamArchive.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

namespace ExamArchive.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class SubjectsController : ControllerBase
{
    private readonly ExamArchiveDbContext _db;

    public SubjectsController(ExamArchiveDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Lists the subjects taught in a major, with the year of study each is taught in.
    /// </summary>
    /// <param name="majorId">
    /// The major to list subjects for. Required — year of study is a property of
    /// the major/subject pairing, so it has no meaning without a major.
    /// </param>
    /// <param name="yearOfStudy">
    /// Narrows the list to one year of the curriculum. Optional, and most clients
    /// will not need it — see the remarks.
    /// </param>
    /// <param name="paging">Which page to return. See <see cref="PageRequest"/>.</param>
    /// <remarks>
    /// A single major's curriculum is short enough to fit one page at the maximum
    /// size, so this is paged for the sake of one response shape across the API
    /// rather than because the list threatens to get long.
    /// <para>
    /// That is also why <paramref name="yearOfStudy"/> is expected to go mostly
    /// unused: a search form asking for a major's whole curriculum at
    /// <c>perPage=100</c> already holds every year on every row, so it can fill both
    /// a year picker and a subject picker from the one response and switch between
    /// them without another request. The filter exists so the endpoint still answers
    /// the question on its own, for a caller that would rather ask than filter.
    /// </para>
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<SubjectDto>>> GetSubjects(
        [FromQuery, BindRequired] int majorId,
        [FromQuery, Range(1, 6, ErrorMessage = "YearOfStudy must be between 1 and 6.")]
        int? yearOfStudy,
        [FromQuery] PageRequest paging,
        CancellationToken cancellationToken)
    {
        // Query starts at the junction rather than at Subject, because the row
        // being filtered on is what carries YearOfStudy.
        var query = _db.MajorSubjects
            .AsNoTracking()
            .Where(ms => ms.MajorId == majorId);

        if (yearOfStudy is not null)
        {
            // Bounded to 1-6 above rather than left to match nothing, because the
            // CK_MajorSubject_YearOfStudy constraint means no row can ever hold a
            // year outside that range — so a request for year 9 is a client bug and
            // an empty page would hide it.
            query = query.Where(ms => ms.YearOfStudy == yearOfStudy);
        }

        // Ordered by year only. Sorting by name belongs to the client, because the
        // right order depends on the language and script the reader chose. SubjectId
        // breaks ties within a year, which paging needs: without it two subjects
        // taught in the same year could land on two pages or on none.
        var subjects = await query
            .OrderBy(ms => ms.YearOfStudy)
            .ThenBy(ms => ms.SubjectId)
            .Select(ms => new SubjectDto(
                ms.SubjectId,
                ms.Subject!.Code,
                ms.Subject.NameSr,
                ms.Subject.NameEn,
                ms.YearOfStudy))
            .ToPagedResultAsync(paging, cancellationToken);

        return Ok(subjects);
    }
}
