using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ExamArchive.Services;

public sealed record SplitQuestion(string Label, string Text);

// Splits extracted paper text on numbered headings. Sub-parts (a), b)) stay in the
// parent body. Preamble before the first heading is discarded. Dates, pass-mark
// warnings and signature lines are not questions, and a number that jumps far
// ahead of the running sequence (a "50." after questions 1–5) is dropped too.
// Numbering may restart at 1 when a new section begins.
public sealed class QuestionSplitter
{
    // A paper can open on a small number, and a missed heading may skip a few.
    // "50." after question 5 is furniture, not the next task.
    private const int MaxStartingNumber = 20;
    private const int MaxQuestionGap = 15;

    private const string Months =
        "januar|februar|mart|april|maj|jun|jul|avgust|septembar|oktobar|novembar|decembar|" +
        "јануар|фебруар|март|април|мај|јун|јул|август|септембар|октобар|новембар|децембар|" +
        "january|february|march|april|may|june|july|august|september|october|november|december";

    // Word headings ("Zadatak 1") allow an optional delimiter; bare numbers ("1.")
    // require one so a year or a decimal is not treated as a question.
    private static readonly Regex QuestionStart = new(
        @"^(?:(?<word>Zadatak|Задатак|Pitanje|Питање|Question)\s+(?<wnum>\d{1,2})\s*[.:)]?|(?<num>\d{1,2})\s*(?:[.)]|[-–—]))\s+",
        RegexOptions.Multiline
            | RegexOptions.IgnoreCase
            | RegexOptions.CultureInvariant
            | RegexOptions.Compiled);

    // "7. 4. 2020. god.", "4th June, 2026", "Јун 2025." A leading "У Нишу," is
    // still the date line at the bottom of the paper.
    private static readonly Regex DateLine = new(
        @"^(?:[uу]\s+\p{L}+\s*,\s*)?(?:" +
        @"\d{1,2}\s*[./]\s*\d{1,2}\s*[./]\s*\d{2,4}|" +
        @"\d{1,2}(?:st|nd|rd|th)?\.?\s+(?:" + Months + @")\s*,?\s*\d{4}|" +
        @"(?:" + Months + @")\s+\d{1,2}(?:st|nd|rd|th)?\s*,?\s*\d{4}|" +
        @"(?:" + Months + @")\s+\d{4}" +
        @")\s*\.?\s*(?:год(?:ине)?|god(?:ine)?|year)?\s*\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // "Note the difference" is a question. "Warning:" and "Напомена 1:" are not.
    private static readonly Regex NoteLine = new(
        @"^(?:напомена|napomena|warning|note)\b(?:\s*\d+)?\s*(?::|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex PassMark = new(
        @"needed\s+\d+\s+points\s+to\s+pass" +
        @"|\d+\s+(?:и|или|i|ili|or)\s+(?:више|vise|više|more)\s+(?:поена|poena|points)" +
        @"|(?:укупно|ukupno)\s+\d+\s+(?:поена|poena)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Titles only. A bare "asistent" would also match a task that mentions one.
    private static readonly Regex Signature = new(
        @"(?:предметни\s+наставник|predmetni\s+nastavnik|предметни\s+асистент|predmetni\s+asistent|виши\s+предавач|visi\s+predavač|visi\s+predavac)" +
        @"|^(?:асистент|asistent|предавач|predavač|predavac)\.?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public IReadOnlyList<SplitQuestion> Split(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var matches = QuestionStart.Matches(text);
        if (matches.Count == 0)
        {
            return [];
        }

        // A rejected heading is still a boundary. "7. 4. 2020." must not be
        // swallowed by the previous task, and it must not become a task either.
        var marks = new List<(Match Match, bool Keep)>(matches.Count);
        var lastNumber = 0;

        foreach (Match match in matches)
        {
            var line = LineAt(text, match.Index);
            var rest = RestOfLine(text, match);
            var number = NumberOf(match);
            var keep = !IsFalseHeading(line, rest) && InSequence(number, lastNumber);
            marks.Add((match, keep));

            if (keep)
            {
                lastNumber = number;
            }
        }

        var questions = new List<SplitQuestion>(marks.Count);

        for (var i = 0; i < marks.Count; i++)
        {
            if (!marks[i].Keep)
            {
                continue;
            }

            var match = marks[i].Match;
            var bodyStart = match.Index + match.Length;
            var bodyEnd = i + 1 < marks.Count ? marks[i + 1].Match.Index : text.Length;
            var body = TrimBoilerplate(text[bodyStart..bodyEnd]);

            if (body.Length == 0)
            {
                continue;
            }

            questions.Add(new SplitQuestion(LabelOf(match), body));
        }

        return questions;
    }

    internal static bool IsQuestionHeadingLine(string line) =>
        QuestionStart.IsMatch(line.Trim());

    private static bool InSequence(int number, int last) =>
        number == 1
        || (last == 0 && number <= MaxStartingNumber)
        || (last > 0 && number > last && number - last <= MaxQuestionGap);

    private static bool IsFalseHeading(string line, string rest) =>
        IsDateLine(line) || IsDateLine(rest)
        || IsNoteLine(line) || IsNoteLine(rest)
        || IsPassMarkLine(line) || IsPassMarkLine(rest)
        || IsSignatureLine(line) || IsSignatureLine(rest);

    private static string TrimBoilerplate(string body)
    {
        var lines = body.Split('\n');
        var start = 0;
        var end = lines.Length;

        while (start < end && IsEdgeLine(lines[start]))
        {
            start++;
        }

        while (end > start && IsEdgeLine(lines[end - 1]))
        {
            end--;
        }

        if (start >= end)
        {
            return string.Empty;
        }

        return string.Join('\n', lines[start..end]).Trim();
    }

    private static bool IsEdgeLine(string line) =>
        string.IsNullOrWhiteSpace(line) || IsBoilerplateLine(line);

    private static bool IsBoilerplateLine(string line) =>
        IsDateLine(line) || IsNoteLine(line) || IsPassMarkLine(line) || IsSignatureLine(line);

    private static bool IsDateLine(string line) => DateLine.IsMatch(line.Trim());

    private static bool IsNoteLine(string line) => NoteLine.IsMatch(line.Trim());

    private static bool IsPassMarkLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 240 || !PassMark.IsMatch(trimmed))
        {
            return false;
        }

        // A task can mention a pass mark in passing. The footer line is the
        // whole sentence: "needed 50 points to pass", "50 и више поена".
        return trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 20;
    }

    // "[15 poena]" belongs to the task. A signature is a short line that is a
    // title, not a sentence that happens to mention one.
    private static bool IsSignatureLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 100 || !Signature.IsMatch(trimmed))
        {
            return false;
        }

        return trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 8;
    }

    private static string LineAt(string text, int index)
    {
        var end = text.IndexOf('\n', index);
        if (end < 0)
        {
            end = text.Length;
        }

        return text[index..end];
    }

    private static string RestOfLine(string text, Match match)
    {
        var line = LineAt(text, match.Index);
        var restStart = match.Length;
        return restStart >= line.Length ? string.Empty : line[restStart..];
    }

    private static int NumberOf(Match match)
    {
        var group = match.Groups["word"].Success ? match.Groups["wnum"] : match.Groups["num"];
        return int.Parse(group.Value, CultureInfo.InvariantCulture);
    }

    private static string LabelOf(Match match)
    {
        var word = match.Groups["word"];
        if (word.Success)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{word.Value} {match.Groups["wnum"].Value}");
        }

        return match.Groups["num"].Value;
    }
}

public static class QuestionText
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public static string Normalize(string text)
    {
        var collapsed = Whitespace.Replace(text.Normalize(NormalizationForm.FormKC), " ");
        return collapsed.Trim().ToLowerInvariant();
    }

    public static string Hash(string text)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(Normalize(text)));

        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
