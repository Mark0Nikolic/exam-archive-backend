namespace ExamArchive.Models;

// A level of study, e.g. "Основне академске студије". The top of the hierarchy.
public class Studies
{
    public int Id { get; set; }

    // Cyrillic is the stored form; Latin is transliterated in the browser, because
    // only that direction is mechanical.
    public string NameSr { get; set; } = string.Empty;

    public string? NameEn { get; set; }

    // How many years this level lasts. Years themselves are 1..YearsOfStudy, not
    // rows: a fourth year is this value going from 3 to 4. Stored rather than
    // implied by the study's name so a bachelor's can grow without a code change.
    public int YearsOfStudy { get; set; }

    public ICollection<Major> Majors { get; set; } = new List<Major>();
}
