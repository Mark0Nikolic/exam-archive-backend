namespace ExamArchive.Services;

/// <summary>
/// Marks an endpoint as reachable by an account that still owes a password change.
/// </summary>
/// <remarks>
/// An allowlist, and short on purpose: it should contain only what somebody needs
/// in order to stop being in this state. Anything not marked is refused, so
/// forgetting the attribute on a new endpoint locks it down rather than opening it
/// — the failure mode worth having.
/// <para>
/// The marker sits on the action rather than in a list of URLs somewhere else,
/// because a list of paths silently stops matching when a route is renamed.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class AllowsPendingPasswordChangeAttribute : Attribute;

/// <summary>
/// Stops an account that is on an administrator-issued password from doing anything
/// except replacing it.
/// </summary>
/// <remarks>
/// This is what makes a temporary password temporary. Without it the flag on the
/// account would be a note nobody reads: the moderator would sign in with the
/// password the admin chose, get straight to work, and never change it — leaving
/// the admin permanently able to act as them.
/// <para>
/// Written as middleware rather than a check inside each controller, because the
/// rule is "everything, except these few" and that is only trustworthy if it is
/// enforced in one place that new endpoints cannot forget to call.
/// </para>
/// </remarks>
public static class PasswordChangeGate
{
    public static IApplicationBuilder UsePasswordChangeGate(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            // Anonymous callers are not in this state and never can be: the flag
            // belongs to an account, and the public archive has no accounts.
            if (context.User.Identity?.IsAuthenticated != true
                || !context.User.HasClaim(UserAccountService.MustChangePasswordClaim, "true"))
            {
                await next();
                return;
            }

            // Requires routing to have run, which it has — the endpoint is resolved
            // before authentication in the default pipeline.
            var allowed = context.GetEndpoint()?
                .Metadata
                .GetMetadata<AllowsPendingPasswordChangeAttribute>() is not null;

            if (allowed)
            {
                await next();
                return;
            }

            // 403 rather than 401: the caller is signed in and their credentials are
            // fine. It is the account that is not yet ready to be used, and 401
            // would send a frontend to a login screen that would succeed and change
            // nothing.
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/problem+json";

            await context.Response.WriteAsync(
                """
                {
                  "title": "Password change required",
                  "status": 403,
                  "detail": "This account is using a password issued by an administrator. Change it at /api/change-password before continuing."
                }
                """);
        });
}
