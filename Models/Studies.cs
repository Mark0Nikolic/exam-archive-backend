namespace ExamArchive.Models;

// A level of study, e.g. "Основне академске студије". The top of the hierarchy.
public class Studies
{
    public int Id { get; set; }

    // Cyrillic is the stored form; Latin is transliterated in the browser, because
    // only that direction is mechanical.
    public string NameSr { get; set; } = string.Empty;

    public string? NameEn { get; set; }

    public ICollection<Major> Majors { get; set; } = new List<Major>();
}
