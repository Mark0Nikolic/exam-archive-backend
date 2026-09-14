namespace ExamArchive.Services;

// Marks an endpoint as reachable by an account that still owes a password change.
// An allowlist: anything unmarked is refused, so forgetting the attribute on a new
// endpoint locks it down rather than opening it.
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class AllowsPendingPasswordChangeAttribute : Attribute;

// Stops an account on an administrator-issued password from doing anything except
// replacing it. Middleware rather than per-controller checks because the rule is
// "everything except these few", which is only trustworthy enforced in one place.
public static class PasswordChangeGate
{
    public static IApplicationBuilder UsePasswordChangeGate(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
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
            // fine, so 401 would send a frontend to a login that would succeed and
            // change nothing.
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
