using System.Text.Json.Serialization;
using ExamArchive.Data;
using ExamArchive.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

#if DEBUG
// A Debug build launched as the .exe defaults to Production and never reads
// appsettings.Development.json. If that file is here, treat the run as local development.
if (builder.Environment.IsProduction()
    && File.Exists(Path.Combine(builder.Environment.ContentRootPath, "appsettings.Development.json")))
{
    builder.Configuration.AddJsonFile("appsettings.Development.json", optional: false, reloadOnChange: true);
    builder.Environment.EnvironmentName = Environments.Development;
}
#endif

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException(
        $"Connection string 'Default' was not found, and the current environment is "
        + $"'{builder.Environment.EnvironmentName}'.\n\n"
        + "If appsettings.Development.json already exists with the string in it, then "
        + "the environment above is the problem rather than the file: that file is only "
        + "read when the environment is Development, and the environment is only set to "
        + "Development by Properties/launchSettings.json — which applies to `dotnet run` "
        + "and to the IDE's run button, but not to launching "
        + "bin/Debug/net10.0/ExamArchive.exe directly. Start it one of those ways, or set "
        + "ASPNETCORE_ENVIRONMENT=Development first.\n\n"
        + "If the file does not exist, create it next to appsettings.json with a "
        + "ConnectionStrings.Default entry of the form "
        + "server=localhost;port=3306;database=examarchive;user=root;password=... "
        + "— or set it with `dotnet user-secrets set ConnectionStrings:Default \"...\"`. "
        + "Never put it in appsettings.json, which is committed to the repository.");

builder.Services.AddDbContext<ExamArchiveDbContext>(options =>
    options.UseMySQL(connectionString));

builder.Services.AddSingleton<PaperFileStorage>();
builder.Services.AddScoped<PaperFileServer>();
builder.Services.AddSingleton<ImageSanitizer>();
builder.Services.AddScoped<PaperSubmissionService>();
builder.Services.AddScoped<UserAccountService>();

// Read above the cookie because the cookie's SameSite mode depends on it.
// Trim and drop a trailing slash: WithOrigins is an exact string match, and a
// copied origin with whitespace or "http://localhost:5173/" silently fails CORS.
var allowedOrigins = (builder.Configuration
        .GetSection("Cors:AllowedOrigins")
        .Get<string[]>() ?? [])
    .Select(origin => origin.Trim().TrimEnd('/'))
    .Where(origin => origin.Length > 0)
    .Distinct(StringComparer.Ordinal)
    .ToArray();

var hasCrossSiteFrontend = allowedOrigins.Length > 0;

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "ExamArchive.Session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;

        // A Lax cookie is not sent on a cross-origin fetch, so a separately-hosted
        // frontend would see sign-in succeed and every later request arrive anonymous.
        // Lax is kept for the same-origin deployment, where it is free CSRF protection.
        options.Cookie.SameSite = hasCrossSiteFrontend
            ? SameSiteMode.None
            : SameSiteMode.Lax;

        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = true;

        // Without these the handler answers an unauthenticated API call with a 302 to a
        // login page that does not exist here.
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };

        // Policy failures answer 401 rather than 403 so a student probing a staff route
        // learns nothing from the status code. A frontend therefore must not treat every
        // 401 as an expired session.
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorization(options => options.AddRolePolicies());

if (hasCrossSiteFrontend)
{
    builder.Services.AddCors(options =>
        options.AddDefaultPolicy(policy => policy
            .WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()
            .WithExposedHeaders("Content-Disposition")));
}

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new UserRoleJsonConverter());
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
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
        var storage = scope.ServiceProvider.GetRequiredService<PaperFileStorage>();

        await SeedData.SeedAsync(db, accounts, storage, app.Logger);
    }

    await AdminBootstrap.EnsureAdminAsync(db, accounts, app.Configuration, app.Logger);
}

// CORS must run before HTTPS redirection. A browser preflight that is redirected
// never retries with Origin, so the real request then looks like it has no
// Access-Control-Allow-Origin header.
if (hasCrossSiteFrontend)
{
    app.UseCors();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.UsePasswordChangeGate();

app.MapControllers();

app.Run();
