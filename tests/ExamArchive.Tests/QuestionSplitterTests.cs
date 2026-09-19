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
