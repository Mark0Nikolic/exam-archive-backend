using ExamArchive.Models;
using Microsoft.AspNetCore.Authorization;

namespace ExamArchive.Services;

// Policies rather than [Authorize(Roles = ...)], which compares the role claim as
// text: with the role carried as a number the attribute would have to read
// Roles = "1,2" and would break silently the day the enum is renumbered.
public static class RolePolicies
{
    public const string Administrators = nameof(Administrators);

    public const string Staff = nameof(Staff);

    public const string SuperAdministrators = nameof(SuperAdministrators);

    public static void AddRolePolicies(this AuthorizationOptions options)
    {
        options.AddPolicy(SuperAdministrators, Require(role => role is UserRole.SuperAdmin));

        options.AddPolicy(Administrators, Require(IsAdministrative));

        options.AddPolicy(Staff, Require(IsStaff));
    }

    public static bool IsAdministrative(UserRole role) =>
        role is UserRole.SuperAdmin or UserRole.Admin;

    // Public because ClaimsPrincipalExtensions.IsStaff asks the same question to
    // decide what a caller may see inside an action the policy never gated.
    public static bool IsStaff(UserRole role) =>
        IsAdministrative(role) || role is UserRole.Moderator;

    private static Action<AuthorizationPolicyBuilder> Require(Func<UserRole, bool> allows) =>
        policy => policy

            // Explicit: without it an anonymous caller merely fails the assertion
            // and is denied, rather than being challenged to sign in.
            .RequireAuthenticatedUser()
            .RequireAssertion(context =>
                context.User.GetRole() is { } role && allows(role));
}
