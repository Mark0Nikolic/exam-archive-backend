namespace ExamArchive.Models;

// Stored as the member name, so these must keep matching the CK_Paper_ExamType
// check constraint.
public enum ExamType
{
    Midterm,

    Final,

    Resit
}
