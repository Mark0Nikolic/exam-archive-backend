using System.Security.Claims;
using ExamArchive.Models;

namespace ExamArchive.Services;

/// <summary>
/// Reads the application's own claims back off a signed-in principal.
/// </summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// The signed-in account's id, or null when the caller is anonymous.
    /// </summary>
    /// <remarks>
    /// Null rather than throwing, because most callers are endpoints that work
    /// either way: an upload is accepted from anyone, and being signed in only
    /// changes whether it is recorded against an account.
    /// <para>
    /// The parse is defensive against a claim that is present but not a number.
    /// That cannot happen from <see cref="UserAccountService.BuildPrincipal"/>, but
    /// the value arrives inside a cookie the server issued and later re-read, and
    /// an id is about to be written into a foreign key column.
    /// </para>
    /// </remarks>
    public static int? GetUserId(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(ClaimTypes.NameIdentifier);

        return int.TryParse(value, out var id) ? id : null;
    }

    /// <summary>
    /// The signed-in account's role, or null when the caller is anonymous or the
    /// cookie does not carry a role this application recognises.
    /// </summary>
    /// <remarks>
    /// A claim value is always a string — the framework has no other kind — so the
    /// number makes the round trip as text and is parsed back here. This is the one
    /// place that happens, which is what keeps the rest of the application dealing
    /// in <see cref="UserRole"/>.
    /// <para>
    /// IsDefined matters as much as the parse: a cookie carrying "9" would otherwise
    /// produce a UserRole that equals no member, pass no policy, and be very hard to
    /// account for while reading the code. Null says "no usable role", and every
    /// caller already has to handle that.
    /// </para>
    /// </remarks>
    public static UserRole? GetRole(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(ClaimTypes.Role);

        return int.TryParse(value, out var number) && Enum.IsDefined(typeof(UserRole), number)
            ? (UserRole)number
            : null;
    }

    /// <summary>
    /// Whether this caller works the review queue, and so may see papers that have
    /// not been approved.
    /// </summary>
    /// <remarks>
    /// The visibility half of the authorization model, kept separate from the
    /// policies that decide whether a request is allowed through at all. Once
    /// browsing and reviewing became one set of routes, "may this caller act" stopped
    /// being the only question — <see cref="PapersController"/> also has to ask "what
    /// does this caller get to see", inside actions that anonymous users reach.
    /// A policy cannot answer that: it decides a request, not a query.
    /// <para>
    /// It defers to <see cref="RolePolicies.IsStaff"/> rather than listing the roles
    /// again, so this and the Staff policy cannot drift into disagreeing about who
    /// staff are — which would show up as a moderator allowed through a route and
    /// then shown the public view of it.
    /// </para>
    /// </remarks>
    public static bool IsStaff(this ClaimsPrincipal principal) =>
        principal.GetRole() is { } role && RolePolicies.IsStaff(role);
}
