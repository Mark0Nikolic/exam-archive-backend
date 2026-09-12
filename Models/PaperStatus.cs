namespace ExamArchive.Models;

// Stored as the member name, so these must keep matching the CK_Paper_Status
// check constraint.
public enum PaperStatus
{
    Pending,

    Approved,

    Rejected
}
