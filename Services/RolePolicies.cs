using ExamArchive.Models;
using Microsoft.AspNetCore.Authorization;

namespace ExamArchive.Services;

/// <summary>
/// The authorization policies endpoints are gated on.
/// </summary>
/// <remarks>
/// Policies rather than <c>[Authorize(Roles = ...)]</c>, because that attribute
/// compares the role claim as text and there is no numeric form of it. With the
/// role carried as a number, the attribute would have to read
/// <c>Roles = "1,2"</c> — correct, unreadable, and silently wrong the day somebody
/// renumbers the enum. A policy is written in terms of <see cref="UserRole"/>
/// itself, so renumbering is a compile-time concern rather than a runtime surprise.
/// <para>
/// Grouped rather than listed per endpoint for the same reason as before: the
/// failure mode of a gate that misses a new role is not an error, it is silently
/// denying the very people it should admit.
/// </para>
/// </remarks>
public static class RolePolicies
{
    /// <summary>Accounts that may manage other accounts and delete from the archive.</summary>
    public const string Administrators = nameof(Administrators);

    /// <summary>Everyone who works the moderation queue.</summary>
    public const string Staff = nameof(Staff);

    /// <summary>The only role that may create, promote or demote an administrator.</summary>
    public const string SuperAdministrators = nameof(SuperAdministrators);

    /// <summary>Registers every policy named above.</summary>
    public static void AddRolePolicies(this AuthorizationOptions options)
    {
        options.AddPolicy(SuperAdministrators, Require(role => role is UserRole.SuperAdmin));

        options.AddPolicy(Administrators, Require(IsAdministrative));

        options.AddPolicy(Staff, Require(IsStaff));
    }

    /// <summary>
    /// Whether <paramref name="role"/> belongs to the administrator tier, which is
    /// the tier only a super administrator may alter.
    /// </summary>
    public static bool IsAdministrative(UserRole role) =>
        role is UserRole.SuperAdmin or UserRole.Admin;

    /// <summary>
    /// Whether <paramref name="role"/> works the review queue.
    /// </summary>
    /// <remarks>
    /// Public, and the policy above is built from it rather than from its own list of
    /// roles, because authorization is no longer the only thing that needs this
    /// answer: since browsing and reviewing became one set of routes,
    /// <see cref="ClaimsPrincipalExtensions.IsStaff"/> asks it again to decide what a
    /// caller may see inside an action the policy never gated. Two lists would
    /// eventually disagree, and the symptom — a role let through a route and then
    /// shown the public view of it — reads like a bug anywhere but here.
    /// </remarks>
    public static bool IsStaff(UserRole role) =>
        IsAdministrative(role) || role is UserRole.Moderator;

    private static Action<AuthorizationPolicyBuilder> Require(Func<UserRole, bool> allows) =>
        policy => policy

            // Explicit, and it decides the status code: without it an anonymous
            // caller merely fails the assertion and is told 403, when the honest
            // answer is 401 and "sign in". (Both are 401 to the client now — see
            // OnRedirectToAccessDenied in Program.cs — but this is what makes the
            // anonymous case a challenge rather than a denial internally.)
            .RequireAuthenticatedUser()
            .RequireAssertion(context =>
                context.User.GetRole() is { } role && allows(role));
}
