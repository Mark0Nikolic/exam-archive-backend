using System.ComponentModel.DataAnnotations;
using ExamArchive.Models;

namespace ExamArchive.Dtos;

/// <summary>
/// Corrects the filing details of a paper already in the archive.
/// </summary>
/// <remarks>
/// A full replacement, as PUT implies: every field is written, so one omitted
/// from the body is reset to its default rather than left alone. That is the
/// honest reading of the verb, and it is why there is no partial variant — a
/// PATCH that distinguished "absent" from "null" would need every property
/// nullable and is not worth the ambiguity for four fields.
/// <para>
/// Moderation state is not here on purpose. Status, ReviewedAt and
/// RejectionReason move together under check constraints that would happily be
/// contradicted by a client sending them independently, so they stay behind the
/// approve and reject endpoints that maintain them as a set.
/// </para>
/// </remarks>
public class UpdatePaperRequest
{
    /// <summary>The subject this paper should be filed under.</summary>
    /// <remarks>
    /// Range rather than Required: Required does not reject a missing value on a
    /// non-nullable int, it binds to zero, and zero is not a subject anybody has.
    /// </remarks>
    [Range(1, int.MaxValue, ErrorMessage = "A subject id is required.")]
    public int SubjectId { get; set; }

    /// <summary>Which sitting the paper is from.</summary>
    public ExamType ExamType { get; set; }

    /// <summary>Month the exam was held, 1-12. Matched by CK_Paper_Month.</summary>
    [Range(1, 12)]
    public int Month { get; set; }

    /// <summary>
    /// Year the exam was held. The lower bound is not a real date, only a floor
    /// low enough to admit anything a university might still hold and high enough
    /// to catch a mistyped or defaulted value.
    /// </summary>
    [Range(1970, 2100)]
    public int Year { get; set; }
}
