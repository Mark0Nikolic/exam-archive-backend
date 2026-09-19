using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ExamArchive.Services;

public sealed record SplitQuestion(string Label, string Text);

// Splits extracted paper text on numbered headings. Sub-parts (a), b)) stay in the
// parent body. Preamble before the first heading is discarded.
public sealed class QuestionSplitter
{
    // Word headings ("Zadatak 1") allow an optional delimiter; bare numbers ("1.")
    // require one so a year or a decimal is not treated as a question.
    private static readonly Regex QuestionStart = new(
        @"^(?:(?<word>Zadatak|Задатак|Pitanje|Питање|Question)\s+(?<wnum>\d{1,2})\s*[.:)]?|(?<num>\d{1,2})\s*(?:[.)]|[-–—]))\s+",
        RegexOptions.Multiline
            | RegexOptions.IgnoreCase
            | RegexOptions.CultureInvariant
            | RegexOptions.Compiled);

    public IReadOnlyList<SplitQuestion> Split(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var matches = QuestionStart.Matches(text);
        if (matches.Count == 0)
        {
            return [];
        }

        var questions = new List<SplitQuestion>(matches.Count);

        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var bodyStart = match.Index + match.Length;
            var bodyEnd = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var body = text[bodyStart..bodyEnd].Trim();

            if (body.Length == 0)
            {
                continue;
            }

            questions.Add(new SplitQuestion(LabelOf(match), body));
        }

        return questions;
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
