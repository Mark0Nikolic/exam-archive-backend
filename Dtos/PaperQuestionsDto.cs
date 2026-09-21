using ExamArchive.Models;

namespace ExamArchive.Dtos;

// One sitting this canonical question has appeared on. Only approved papers are
// listed, so a pending parse cannot leak another student's submission.
public record QuestionAppearanceDto(
    int PaperId,
    ExamType ExamType,
    int Month,
    int Year,
    int Ordinal,
    string Label);

public record PaperQuestionDto(
    int QuestionId,
    int Ordinal,
    string Label,
    string Text,
    IReadOnlyList<QuestionAppearanceDto> Appearances,
    bool AppearedRecently);

public record PaperQuestionsDto(
    int PaperId,
    PaperParseStatus ParseStatus,
    string? ParseError,
    IReadOnlyList<PaperQuestionDto> Questions);
