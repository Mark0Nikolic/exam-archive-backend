using ExamArchive.Models;

namespace ExamArchive.Dtos;

/// <summary>
/// An approved exam paper. Status is deliberately absent — this DTO is only ever
/// built from approved rows, so exposing the field would imply a filter the
/// browse API does not offer.
/// </summary>
/// <param name="SubjectNameSr">
/// Carried on the row rather than left to a second request, because the browse
/// list can now span subjects and a paper that only knows its subject's id is
/// unrenderable without one lookup per row.
/// </param>
/// <param name="PageCount">
/// How many files make up the paper. A listing needs this to show "4 pages"
/// without fetching the pages themselves, and a client needs it to know the
/// range of page numbers it may request.
/// </param>
public record PaperDto(
    int Id,
    int SubjectId,
    string SubjectNameSr,
    string? SubjectNameEn,
    ExamType ExamType,
    int Month,
    int Year,
    int PageCount,
    DateTime UploadedAt);
