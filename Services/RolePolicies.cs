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
        options.AddPolicy(SuperAdministrators, Require(UserRole.SuperAdmin));

        options.AddPolicy(Administrators, Require(UserRole.SuperAdmin, UserRole.Admin));

        options.AddPolicy(
            Staff,
            Require(UserRole.SuperAdmin, UserRole.Admin, UserRole.Moderator));
    }

    /// <summary>
    /// Whether <paramref name="role"/> belongs to the administrator tier, which is
    /// the tier only a super administrator may alter.
    /// </summary>
    public static bool IsAdministrative(UserRole role) =>
        role is UserRole.SuperAdmin or UserRole.Admin;

    private static Action<AuthorizationPolicyBuilder> Require(params UserRole[] roles) =>
        policy => policy

            // Explicit, and it decides the status code: without it an anonymous
            // caller merely fails the assertion and is told 403, when the honest
            // answer is 401 and "sign in".
            .RequireAuthenticatedUser()
            .RequireAssertion(context =>
                context.User.GetRole() is { } role && roles.Contains(role));
}
