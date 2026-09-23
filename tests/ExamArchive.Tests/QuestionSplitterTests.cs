using ExamArchive.Services;

namespace ExamArchive.Tests;

public sealed class QuestionSplitterTests
{
    private readonly QuestionSplitter _splitter = new();

    [Fact]
    public void SplitsEnglishNumberedQuestionsAndDropsPreamble()
    {
        var questions = _splitter.Split(
            """
            Databases — Final exam
            Name: ________

            1. What is a primary key?
            2. Define a foreign key.
            """);

        Assert.Equal(2, questions.Count);
        Assert.Equal("1", questions[0].Label);
        Assert.Equal("What is a primary key?", questions[0].Text);
        Assert.Equal("2", questions[1].Label);
        Assert.Equal("Define a foreign key.", questions[1].Text);
    }

    [Theory]
    [InlineData("Zadatak 1 Opisite SELECT.\nZadatak 2 Nacrtajte ER.", "Zadatak 1", "Zadatak 2")]
    [InlineData("Задатак 1 Опишите SELECT.\nЗадатак 2 Нацртајте ER.", "Задатак 1", "Задатак 2")]
    [InlineData("Pitanje 1 Sta je kljuc?\nPitanje 2 Sta je indeks?", "Pitanje 1", "Pitanje 2")]
    [InlineData("Питање 1 Шта је кључ?\nПитање 2 Шта је индекс?", "Питање 1", "Питање 2")]
    [InlineData("Question 1 What is SQL?\nQuestion 2 What is DDL?", "Question 1", "Question 2")]
    public void SplitsNamedHeadings(string text, string firstLabel, string secondLabel)
    {
        var questions = _splitter.Split(text);

        Assert.Equal(2, questions.Count);
        Assert.Equal(firstLabel, questions[0].Label);
        Assert.Equal(secondLabel, questions[1].Label);
    }

    [Fact]
    public void KeepsSubpartsInsideTheParentQuestion()
    {
        var questions = _splitter.Split(
            """
            1. Normalisation
            a) Define 1NF.
            b) Define 2NF.
            2. Transactions
            """);

        Assert.Equal(2, questions.Count);
        Assert.Contains("a) Define 1NF.", questions[0].Text);
        Assert.Contains("b) Define 2NF.", questions[0].Text);
        Assert.Equal("Transactions", questions[1].Text);
    }

    [Fact]
    public void ReturnsNothingWhenThereAreNoNumberedQuestions()
    {
        Assert.Empty(_splitter.Split("This paper is a photograph of a whiteboard."));
        Assert.Empty(_splitter.Split(""));
        Assert.Empty(_splitter.Split("4th June, 2026\n7. 4. 2020. god."));
    }

    [Fact]
    public void SplitsFiveNumberedQuestionsAndKeepsPointTags()
    {
        var questions = _splitter.Split(
            """
            1. [10 poena] Dato je N tacaka.
            2. [15 poena] Napisati funkciju.
            3. [15 poena] Nacrtati algoritam.
            4. [15 poena] Formirati matricu.
            5. [15 poena] Odstampati imena.
            """);

        Assert.Equal(["1", "2", "3", "4", "5"], questions.Select(q => q.Label));
        Assert.Contains("[10 poena]", questions[0].Text);
        Assert.Contains("[15 poena]", questions[4].Text);
    }

    [Fact]
    public void SplitsAllCapsCyrillicHeadingsAndKeepsCyrillicSubparts()
    {
        var questions = _splitter.Split(
            """
            ЗАДАТАК 1. Карте у шпилу.
            а) Измешати карте.
            б) Избацити карту.
            ЗАДАТАК 2. Власник имања.
            """);

        Assert.Equal(2, questions.Count);
        Assert.Equal("ЗАДАТАК 1", questions[0].Label);
        Assert.Contains("а) Измешати карте.", questions[0].Text);
        Assert.Contains("б) Избацити карту.", questions[0].Text);
        Assert.Equal("ЗАДАТАК 2", questions[1].Label);
    }

    [Fact]
    public void DatesAreNotQuestions()
    {
        var questions = _splitter.Split(
            """
            1. What is a primary key?
            4th June, 2026
            7. 4. 2020. god.
            U Nisu, 7. 4. 2020. god.
            Jun 2025.
            2. Define a foreign key.
            """);

        Assert.Equal(2, questions.Count);
        Assert.Equal("What is a primary key?", questions[0].Text);
        Assert.Equal("Define a foreign key.", questions[1].Text);
    }

    [Fact]
    public void PassMarkWarningsAreNotAppendedToTheLastQuestion()
    {
        var questions = _splitter.Split(
            """
            1. What is a primary key?
            2. Define a foreign key.

            Napomena: Ukupno 100 poena. Student je polozio ako ima 50 i vise poena.
            Warning: needed 50 points to pass the exam.
            Напомена: Студент би положио ако има 50 и више поена.
            Predmetni nastavnik
            """);

        Assert.Equal(2, questions.Count);
        Assert.Equal("Define a foreign key.", questions[1].Text);
    }

    [Fact]
    public void TrailingPassSentenceIsRemovedButTheTaskItselfStays()
    {
        var questions = _splitter.Split(
            """
            1. [15 poena] Odštampati imena studenata koji su položili ispit.
            Ispit su položili svi studenti koji imaju više od 50 poena.
            Pera Peric 30

            Ispit su položili svi studenti koji na ispitu osvoje 35 ili više poena.
            """);

        Assert.Single(questions);
        Assert.Contains("više od 50 poena", questions[0].Text);
        Assert.Contains("Pera Peric 30", questions[0].Text);
        Assert.DoesNotContain("35 ili više", questions[0].Text);
    }

    [Fact]
    public void NumberingMayRestartAtOneForANewSection()
    {
        var questions = _splitter.Split(
            """
            1. Practical one.
            2. Practical two.

            Teorijski deo
            1. Define Lorentz force.
            2. Define inductance.
            """);

        Assert.Equal(["1", "2", "1", "2"], questions.Select(q => q.Label));
        Assert.Equal("Define Lorentz force.", questions[2].Text);
    }

    [Fact]
    public void FarNumberJumpIsNotAQuestion()
    {
        var questions = _splitter.Split(
            """
            1. First task.
            2. Second task.
            50. needed 50 points to pass the exam.
            """);

        Assert.Equal(2, questions.Count);
        Assert.Equal("Second task.", questions[1].Text);
    }

    [Fact]
    public void RepeatedPageHeaderIsNotPartOfTheQuestionThatCrossesThePage()
    {
        var pages = PaperTextExtractor.WithoutSharedBands(
        [
            """
            AKADEMIJA TEHNICKIH STUDIJA
            KATEDRA ZA IT
            Jun 2025.

            1. What is a primary key?
            2. Define a foreign key.
            """,
            """
            AKADEMIJA TEHNICKIH STUDIJA
            KATEDRA ZA IT
            Jun 2025.

            3. What is SQL?
            Predmetni nastavnik
            """
        ]);

        var questions = _splitter.Split(string.Join('\n', pages));

        Assert.Equal(["1", "2", "3"], questions.Select(q => q.Label));
        Assert.DoesNotContain("AKADEMIJA", questions[1].Text);
        Assert.Equal("What is SQL?", questions[2].Text);
    }

    [Fact]
    public void RepeatedMatrixRowInsideOneQuestionIsKept()
    {
        var pages = PaperTextExtractor.WithoutSharedBands(
        [
            """
            KATEDRA ZA IT

            1. Formirati matricu
            1 1 1 1 1 1 1 1 1
            1 0 0 0 1 0 0 0 1
            1 1 1 1 1 1 1 1 1
            """,
            """
            KATEDRA ZA IT

            2. Sledeci zadatak.
            """
        ]);

        var questions = _splitter.Split(string.Join('\n', pages));

        Assert.Equal(2, questions.Count);
        Assert.Equal(2, questions[0].Text.Split('\n').Count(line => line.Contains("1 1 1 1 1 1 1 1 1")));
    }

    [Fact]
    public void HashIgnoresWhitespaceAndCase()
    {
        var left = QuestionText.Hash("What is a  primary KEY?");
        var right = QuestionText.Hash("what is a primary key?");

        Assert.Equal(left, right);
        Assert.Equal(64, left.Length);
    }
}
