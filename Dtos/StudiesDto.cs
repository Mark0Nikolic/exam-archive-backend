namespace ExamArchive.Dtos;

/// <summary>
/// A level of study as returned by the browse API. Flat by design — no navigation
/// properties, so the serializer can never walk down into Majors.
/// </summary>
/// <param name="NameSr">
/// The Serbian name, in Cyrillic. Always present. A client showing Latin
/// transliterates this rather than asking the server for it — see
/// <see cref="MajorDto"/> for why that direction is the only reliable one.
/// </param>
/// <param name="NameEn">
/// The English name, or null where none is recorded. Clients fall back to
/// <paramref name="NameSr"/> rather than showing a blank.
/// </param>
public record StudiesDto(int Id, string NameSr, string? NameEn);
