using ExamArchive.Models;

namespace ExamArchive.Dtos;

public record PaperQuestionDto(int Ordinal, string Label, string Text);

public record PaperQuestionsDto(
    int PaperId,
    PaperParseStatus ParseStatus,
    string? ParseError,
    IReadOnlyList<PaperQuestionDto> Questions);
