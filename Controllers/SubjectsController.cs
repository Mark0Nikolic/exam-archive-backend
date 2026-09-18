using System.ComponentModel.DataAnnotations;
using ExamArchive.Data;
using ExamArchive.Dtos;
using ExamArchive.Models;
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
    private readonly ILogger<SubjectsController> _logger;

    public SubjectsController(ExamArchiveDbContext db, ILogger<SubjectsController> logger)
    {
        _db = db;
        _logger = logger;
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

    // Subjects that are not taught in any major: a course the curriculum dropped,
    // still sitting in the catalogue (and possibly still holding papers). Year of
    // study is a pairing property, so it is 0 here.
    [HttpGet("unattached")]
    [Authorize(Policy = RolePolicies.Administrators)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PagedResult<SubjectDto>>> GetUnattachedSubjects(
        [FromQuery] PageRequest paging,
        CancellationToken cancellationToken)
    {
        var subjects = await _db.Subjects
            .AsNoTracking()
            .Where(s => !s.MajorSubjects.Any())
            .OrderBy(s => s.Id)
            .Select(s => new SubjectDto(s.Id, s.Code, s.NameSr, s.NameEn, 0))
            .ToPagedResultAsync(paging, cancellationToken);

        return Ok(subjects);
    }

    [HttpPost]
    [Authorize(Policy = RolePolicies.Administrators)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SubjectDto>> CreateSubject(
        [FromBody] CreateSubjectRequest request,
        CancellationToken cancellationToken)
    {
        var yearsOfStudy = await _db.Majors
            .AsNoTracking()
            .Where(m => m.Id == request.MajorId)
            .Select(m => (int?)m.Studies!.YearsOfStudy)
            .FirstOrDefaultAsync(cancellationToken);

        if (yearsOfStudy is null)
        {
            ModelState.AddModelError(
                nameof(request.MajorId),
                $"No major with id {request.MajorId} exists.");

            return ValidationProblem(ModelState);
        }

        if (request.YearOfStudy > yearsOfStudy)
        {
            ModelState.AddModelError(
                nameof(request.YearOfStudy),
                $"Year of study {request.YearOfStudy} is outside the {yearsOfStudy} years of major {request.MajorId}.");

            return ValidationProblem(ModelState);
        }

        var code = OptionalName(request.Code);

        if (await CodeTakenAsync(code, exceptSubjectId: null, cancellationToken))
        {
            return CodeConflict(code!);
        }

        var subject = new Subject
        {
            NameSr = request.NameSr.Trim(),
            NameEn = OptionalName(request.NameEn),
            Code = code,
            MajorSubjects =
            [
                new MajorSubject
                {
                    MajorId = request.MajorId,
                    YearOfStudy = request.YearOfStudy
                }
            ]
        };

        _db.Subjects.Add(subject);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "{Admin} created subject {SubjectId} in major {MajorId} year {Year}.",
            User.Identity?.Name,
            subject.Id,
            request.MajorId,
            request.YearOfStudy);

        var dto = new SubjectDto(
            subject.Id,
            subject.Code,
            subject.NameSr,
            subject.NameEn,
            request.YearOfStudy);

        return CreatedAtAction(
            nameof(GetSubjects),
            new { majorId = request.MajorId },
            dto);
    }

    [HttpPut("{id:int}")]
    [Authorize(Policy = RolePolicies.Administrators)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SubjectDto>> UpdateSubject(
        int id,
        [FromBody] UpdateSubjectRequest request,
        CancellationToken cancellationToken)
    {
        if (request.MajorId is null != request.YearOfStudy is null)
        {
            ModelState.AddModelError(
                request.MajorId is null ? nameof(request.MajorId) : nameof(request.YearOfStudy),
                "MajorId and YearOfStudy must be sent together to change the year in a major.");

            return ValidationProblem(ModelState);
        }

        var subject = await _db.Subjects.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

        if (subject is null)
        {
            return NotFound();
        }

        var code = OptionalName(request.Code);

        if (await CodeTakenAsync(code, exceptSubjectId: id, cancellationToken))
        {
            return CodeConflict(code!);
        }

        MajorSubject? link = null;

        if (request.MajorId is not null)
        {
            var yearsOfStudy = await _db.Majors
                .AsNoTracking()
                .Where(m => m.Id == request.MajorId)
                .Select(m => (int?)m.Studies!.YearsOfStudy)
                .FirstOrDefaultAsync(cancellationToken);

            if (yearsOfStudy is null)
            {
                ModelState.AddModelError(
                    nameof(request.MajorId),
                    $"No major with id {request.MajorId} exists.");

                return ValidationProblem(ModelState);
            }

            if (request.YearOfStudy > yearsOfStudy)
            {
                ModelState.AddModelError(
                    nameof(request.YearOfStudy),
                    $"Year of study {request.YearOfStudy} is outside the {yearsOfStudy} years of major {request.MajorId}.");

                return ValidationProblem(ModelState);
            }

            link = await _db.MajorSubjects.FirstOrDefaultAsync(
                ms => ms.MajorId == request.MajorId && ms.SubjectId == id,
                cancellationToken);

            if (link is null)
            {
                ModelState.AddModelError(
                    nameof(request.MajorId),
                    $"Subject {id} is not taught in major {request.MajorId}.");

                return ValidationProblem(ModelState);
            }

            link.YearOfStudy = request.YearOfStudy!.Value;
        }

        subject.NameSr = request.NameSr.Trim();
        subject.NameEn = OptionalName(request.NameEn);
        subject.Code = code;

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("{Admin} updated subject {SubjectId}.", User.Identity?.Name, id);

        var yearOfStudy = link?.YearOfStudy
            ?? await _db.MajorSubjects
                .Where(ms => ms.SubjectId == id)
                .Select(ms => (int?)ms.YearOfStudy)
                .FirstOrDefaultAsync(cancellationToken)
            ?? 0;

        return Ok(new SubjectDto(
            subject.Id,
            subject.Code,
            subject.NameSr,
            subject.NameEn,
            yearOfStudy));
    }

    [HttpDelete("{id:int}")]
    [Authorize(Policy = RolePolicies.Administrators)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteSubject(int id, CancellationToken cancellationToken)
    {
        var subject = await _db.Subjects.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

        if (subject is null)
        {
            return NotFound();
        }

        var hasPapers = await _db.Papers.AnyAsync(p => p.SubjectId == id, cancellationToken);

        if (hasPapers)
        {
            return Problem(
                title: "Subject in use",
                detail: $"Subject {id} still has papers. Delete those first.",
                statusCode: StatusCodes.Status409Conflict);
        }

        _db.Subjects.Remove(subject);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("{Admin} deleted subject {SubjectId}.", User.Identity?.Name, id);

        return NoContent();
    }

    private async Task<bool> CodeTakenAsync(
        string? code,
        int? exceptSubjectId,
        CancellationToken cancellationToken)
    {
        if (code is null)
        {
            return false;
        }

        if (exceptSubjectId is null)
        {
            return await _db.Subjects.AnyAsync(s => s.Code == code, cancellationToken);
        }

        return await _db.Subjects.AnyAsync(
            s => s.Code == code && s.Id != exceptSubjectId.Value,
            cancellationToken);
    }

    private ObjectResult CodeConflict(string code) =>
        Problem(
            title: "Subject code taken",
            detail: $"A subject with code '{code}' already exists.",
            statusCode: StatusCodes.Status409Conflict);

    private static string? OptionalName(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
