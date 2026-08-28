namespace ExamArchive.Models;

/// <summary>
/// What an account is allowed to do. One role per account, deliberately.
/// </summary>
/// <remarks>
/// Three strictly nested levels: a submitter, someone who reviews what submitters
/// send, and someone who configures the archive they both use. Because they nest,
/// a set of roles per account would be the more flexible design and the wrong one
/// — an account holding both Moderator and Admin says nothing that Admin does not
/// already say. A single column also lets the database enforce the value with a
/// check constraint, which a join table cannot.
/// <para>
/// Stored as text for the same reason as <see cref="PaperStatus"/> — roles have no
/// natural order, so a number would be an arbitrary code that has to be looked up
/// every time someone reads the table by eye.
/// </para>
/// <para>
/// The declaration order is load-bearing even though the numbers never reach the
/// database. <c>[Required]</c> on a non-nullable enum does not reject a missing
/// value — it binds to zero — so whichever member is written first is what an
/// omitted role silently becomes. Least privilege goes first so that omission
/// fails safe.
/// </para>
/// </remarks>
public enum UserRole
{
    /// <summary>
    /// A student with an account. Submits papers to the queue and follows what
    /// becomes of them, and has no say over anybody else's submission.
    /// </summary>
    /// <remarks>
    /// The role every registration produces, and the reason uploading stopped being
    /// anonymous: an open endpoint accepts spam and misleading files as readily as
    /// exam papers, and a name attached to a submission is what makes a pattern of
    /// them answerable.
    /// </remarks>
    User,

    /// <summary>
    /// Reviews the submission queue: approve, reject, and publish directly. The
    /// operational role — it acts on papers moving through the archive.
    /// </summary>
    Moderator,

    /// <summary>
    /// Everything a moderator can do, plus the structural work: managing accounts
    /// and the subject catalogue that papers are filed against.
    /// </summary>
    /// <remarks>
    /// The line between the two is what each one edits. A moderator judges the
    /// papers; an admin edits the things papers are judged against, where a mistake
    /// affects the whole archive rather than one submission.
    /// </remarks>
    Admin
}
