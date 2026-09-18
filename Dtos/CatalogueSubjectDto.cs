namespace ExamArchive.Dtos;

public sealed record SubjectPlacementDto(
    int MajorId,
    string MajorNameSr,
    string? MajorNameEn,
    int StudiesId,
    string StudiesNameSr,
    string? StudiesNameEn,
    int YearOfStudy);

public sealed record CatalogueSubjectDto(
    int Id,
    string? Code,
    string NameSr,
    string? NameEn,
    IReadOnlyList<SubjectPlacementDto> Placements);
