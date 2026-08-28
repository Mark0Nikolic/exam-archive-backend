using System.Text.Json.Serialization;
using ExamArchive.Data;
using ExamArchive.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Deliberately absent from appsettings.json, which is committed: this string
// carries a database password. It belongs in appsettings.Development.json or in
// user-secrets, both of which stay on the machine that owns them.
//
// The message spells out the shape because the file it asks for is gitignored,
// so a fresh clone has nothing to copy and this exception is the only instruction
// anybody gets.
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException(
        "Connection string 'Default' was not found. Create appsettings.Development.json "
        + "next to appsettings.json containing: "
        + "{\"ConnectionStrings\":{\"Default\":\"server=localhost;port=3306;"
        + "database=examarchive;user=examarchive;password=YOUR_PASSWORD\"}} "
        + "— or set it with `dotnet user-secrets set ConnectionStrings:Default \"...\"`. "
        + "Never put it in appsettings.json, which is committed to the repository.");

builder.Services.AddDbContext<ExamArchiveDbContext>(options =>
    options.UseMySQL(connectionString));

// Storage is stateless and resolves its root once, so a singleton. The server
// wraps a DbContext and has to follow its scope.
builder.Services.AddSingleton<PaperFileStorage>();
builder.Services.AddScoped<PaperFileServer>();
builder.Services.AddSingleton<ImageSanitizer>();
builder.Services.AddScoped<PaperSubmissionService>();
builder.Services.AddScoped<UserAccountService>();

// Sessions are cookies. See AuthController for why, rather than bearer tokens.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        // Named rather than left as the default ".AspNetCore.Cookies", which
        // announces the framework to anyone reading response headers.
        options.Cookie.Name = "ExamArchive.Session";

        // The one that matters: JavaScript cannot read this cookie, so an XSS flaw
        // in the frontend cannot steal a moderator's session. It is the default,
        // and it is set explicitly because it is the reason cookies were chosen.
        options.Cookie.HttpOnly = true;

        // Never sent over plain HTTP, so the session cannot be lifted off the wire.
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;

        // The frontend runs on its own origin during development — a React dev
        // server on localhost:5173 talking to this API on localhost:7294 — and a
        // Lax cookie is not sent on a cross-origin fetch, so sign-in would appear
        // to succeed and every later request would arrive anonymous. None permits
        // it, and is safe only because Secure above is also set.
        //
        // In production the frontend is served from this origin, where Lax is
        // correct and is the CSRF defence: a form on another site cannot cause the
        // browser to attach this cookie to a cross-site POST.
        options.Cookie.SameSite = builder.Environment.IsDevelopment()
            ? SameSiteMode.None
            : SameSiteMode.Lax;

        // Sliding, so the clock restarts on each request: a moderator working
        // through the queue stays signed in, and an abandoned session expires.
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;

        // Without these, the handler answers an unauthenticated API call with a 302
        // to a login page that does not exist here. A fetch() follows the redirect
        // and reports 404, so the frontend sees a missing page instead of "you are
        // not signed in". Status codes are what an API is supposed to return.
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };

        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorization();

// The browser blocks a cross-origin request unless the server names the origin it
// came from, which every request from a separately-hosted frontend is. Origins are
// configured rather than hardcoded because they differ per machine and per tool —
// Vite defaults to 5173, Create React App to 3000 — and because a wildcard is not
// an option: a policy carrying credentials must name its origins explicitly, and
// the browser enforces that.
var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? [];

if (allowedOrigins.Length > 0)
{
    builder.Services.AddCors(options =>
        options.AddDefaultPolicy(policy => policy
            .WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()

            // Without this the browser strips the session cookie from cross-origin
            // requests, and the frontend must match it with credentials: 'include'.
            // Both sides have to agree or the cookie is silently not sent.
            .AllowCredentials()

            // Content-Disposition is not one of the headers a cross-origin script
            // may read by default, so without this the download name never reaches
            // the frontend and saved files fall back to the URL's last segment.
            .WithExposedHeaders("Content-Disposition")));
}

// Enums serialize as their name, not their number. Without this, PaperStatus
// would reach clients as 0/1/2 — unreadable, and it would silently change the
// shape of responses that have always carried "Pending".
builder.Services.AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI();
}

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ExamArchiveDbContext>();
    var accounts = scope.ServiceProvider.GetRequiredService<UserAccountService>();

    if (app.Environment.IsDevelopment())
    {
        // Sample data for local development only. No-op once the database has rows.
        // This branch is the only thing standing between the seeded accounts and a
        // real deployment, which is why the accounts are created here and not in a
        // migration.
        var storage = scope.ServiceProvider.GetRequiredService<PaperFileStorage>();

        await SeedData.SeedAsync(db, accounts, storage, app.Logger);
    }

    // Unconditional, unlike the seeding above: this is the production path, and it
    // is the only way an administrator can exist on a machine that has never had
    // one. Runs after seeding so that a development database, which already has a
    // seeded admin, falls straight through without a warning.
    await AdminBootstrap.EnsureAdminAsync(db, accounts, app.Configuration, app.Logger);
}

app.UseHttpsRedirection();

// Before authentication, so the browser's preflight OPTIONS request — which never
// carries the session cookie — is answered rather than rejected as unauthorized.
if (allowedOrigins.Length > 0)
{
    app.UseCors();
}

// Order is load-bearing and this is the usual place to get it wrong.
// UseAuthentication reads the cookie and builds HttpContext.User; UseAuthorization
// then decides whether that user may proceed. Reversed, every [Authorize] endpoint
// would be judging an anonymous principal and would refuse a signed-in moderator.
app.UseAuthentication();
app.UseAuthorization();

// After authorization, so it judges a caller who has already been identified and
// permitted. An account on an administrator-issued password gets no further than
// changing it.
app.UsePasswordChangeGate();

app.MapControllers();

app.Run();
