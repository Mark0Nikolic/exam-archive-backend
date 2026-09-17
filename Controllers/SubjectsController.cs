using System.ComponentModel.DataAnnotations;
using ExamArchive.Data;
using ExamArchive.Dtos;
using ExamArchive.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

namespace ExamArchive.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
[Authorize]
public class SubjectsController : ControllerBase
{
    private readonly ExamArchiveDbContext _db;

    public SubjectsController(ExamArchiveDbContext db)
    {
        _db = db;
    }

    // majorId is required: year of study is a property of the major/subject pairing,
    // so it has no meaning without a major.
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PagedResult<SubjectDto>>> GetSubjects(
        [FromQuery, BindRequired] int majorId,
        [FromQuery, Range(1, int.MaxValue, ErrorMessage = "YearOfStudy must be at least 1.")]
        int? yearOfStudy,
        [FromQuery] PageRequest paging,
        CancellationToken cancellationToken)
    {
        // Starts at the junction rather than at Subject, because that is the row
        // carrying YearOfStudy.
        var query = _db.MajorSubjects
            .AsNoTracking()
            .Where(ms => ms.MajorId == majorId);

        if (yearOfStudy is not null)
        {
            // Ceiling is the parent study's length, not a global 1–6. An unknown
            // major is left to return an empty page, same as an unknown majorId
            // with no year: there is nothing to measure the year against.
            var yearsOfStudy = await _db.Majors
                .AsNoTracking()
                .Where(m => m.Id == majorId)
                .Select(m => (int?)m.Studies!.YearsOfStudy)
                .FirstOrDefaultAsync(cancellationToken);

            if (yearsOfStudy is not null && yearOfStudy > yearsOfStudy)
            {
                ModelState.AddModelError(
                    "yearOfStudy",
                    $"Year of study {yearOfStudy} is outside the {yearsOfStudy} years of major {majorId}.");

                return ValidationProblem(ModelState);
            }

            query = query.Where(ms => ms.YearOfStudy == yearOfStudy);
        }

        // Ordered by year only; sorting by name belongs to the client. SubjectId
        // breaks ties within a year, which paging needs.
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
