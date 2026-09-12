using System.Security.Claims;
using ExamArchive.Models;

namespace ExamArchive.Services;

public static class ClaimsPrincipalExtensions
{
    public static int? GetUserId(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(ClaimTypes.NameIdentifier);

        return int.TryParse(value, out var id) ? id : null;
    }

    // A claim value is always a string, so the role number makes the round trip as
    // text and is parsed back here. IsDefined matters as much as the parse: a cookie
    // carrying "9" would otherwise produce a UserRole equal to no member.
    public static UserRole? GetRole(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(ClaimTypes.Role);

        return int.TryParse(value, out var number) && Enum.IsDefined(typeof(UserRole), number)
            ? (UserRole)number
            : null;
    }

    // The visibility half of the authorization model: a policy decides a request,
    // not a query, and PapersController serves both publics from one route. Defers
    // to RolePolicies.IsStaff so the two cannot drift apart.
    public static bool IsStaff(this ClaimsPrincipal principal) =>
        principal.GetRole() is { } role && RolePolicies.IsStaff(role);
}
