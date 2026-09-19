namespace ExamArchive.Models;

// Stored as the member name, so these must keep matching the CK_Paper_ParseStatus
// check constraint.
public enum PaperParseStatus
{
    NotQueued,

    Queued,

    Parsed,

    Skipped,

    Failed
}
