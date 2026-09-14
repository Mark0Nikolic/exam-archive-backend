namespace ExamArchive.Dtos;

// YearOfStudy comes from the MajorSubject junction row, so the same subject can
// appear with a different year depending on which major was requested.
public record SubjectDto(
    int Id,
    string? Code,
    string NameSr,
    string? NameEn,
    int YearOfStudy);
