using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using CodeArcheologist.Api.Security;
using CodeArcheologist.Api.Workspaces;

var builder = WebApplication.CreateBuilder(args);

// Security invariant: never listen on LAN or wildcard addresses by default.
builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(5127));

builder.Services.AddSingleton<LocalAccessState>();
builder.Services.AddSingleton<WorkspaceStore>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "CodeArcheologist.Session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        // HTTP is permitted only on loopback; remote hosting will require HTTPS.
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
        options.SlidingExpiration = false;
        options.Events.OnValidatePrincipal = context =>
        {
            var state = context.HttpContext.RequestServices.GetRequiredService<LocalAccessState>();
            if (context.Principal?.FindFirst("local_process")?.Value != state.InstanceId ||
                !state.IsSessionActive(context.Principal?.FindFirst("local_session")?.Value))
            {
                context.RejectPrincipal();
            }
            return Task.CompletedTask;
        };
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

var app = builder.Build();

// The bootstrap secret is generated in memory and announced only on the local
// process console; the start scripts provide an easier interactive path.
// Initialize the versioned metadata database before accepting requests.
// A migration failure stops startup rather than silently losing user data.
app.Services.GetRequiredService<WorkspaceStore>().Initialize();

var localAccess = app.Services.GetRequiredService<LocalAccessState>();
app.Lifetime.ApplicationStarted.Register(() =>
    Console.WriteLine($"Code Archeologist local pairing code: {localAccess.BootstrapCode}"));

app.UseAuthentication();
app.UseMiddleware<LocalRequestGuard>();
app.UseAuthorization();

app.MapGet("/api/v1/health", () =>
    Results.Ok(new { status = "ok", component = "api", stage = "local-access" }))
    .WithName("GetHealth");

app.MapPost("/api/v1/auth/login", async (LoginRequest request, HttpContext context,
    LocalAccessState access) =>
{
    if (!access.TryPair(request.Code))
    {
        return Results.Unauthorized();
    }

    var csrf = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    var sessionId = access.CreateSession();
    var identity = new ClaimsIdentity(
    [
        new Claim(ClaimTypes.Name, "local-owner"),
        new Claim("local_process", access.InstanceId),
        new Claim("local_session", sessionId),
        new Claim(LocalRequestGuard.CsrfClaimType, csrf)
    ], CookieAuthenticationDefaults.AuthenticationScheme);

    await context.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity));

    return Results.Ok(new { authenticated = true, csrfToken = csrf });
});

app.MapGet("/api/v1/auth/session", (HttpContext context) =>
    Results.Ok(new
    {
        authenticated = true,
        csrfToken = context.User.FindFirst(LocalRequestGuard.CsrfClaimType)?.Value
    }))
    .RequireAuthorization();

app.MapPost("/api/v1/auth/logout", async (HttpContext context,
    LocalAccessState access) =>
{
    access.RevokeSession(context.User.FindFirst("local_session")?.Value);
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
}).RequireAuthorization();

// Workspace metadata only. No file paths, repository content or model output
// are accepted by these endpoints. All routes require a paired browser session.
app.MapGet("/api/v1/workspaces", (WorkspaceStore store) =>
    Results.Ok(store.List(limit: 100)))
    .RequireAuthorization();

app.MapGet("/api/v1/workspaces/{id:guid}", (Guid id, WorkspaceStore store) =>
{
    var workspace = store.Get(id);
    return workspace is null ? Results.NotFound() : Results.Ok(workspace);
}).RequireAuthorization();

app.MapPost("/api/v1/workspaces", (CreateWorkspaceRequest request,
    HttpContext context, WorkspaceStore store) =>
{
    var name = request.Name?.Trim();
    if (string.IsNullOrWhiteSpace(name) || name.Length > 120 ||
        name.Any(char.IsControl))
    {
        return Results.BadRequest(new { error = "Workspace name must contain 1 to 120 printable characters." });
    }

    if (!Guid.TryParseExact(context.Request.Headers["Idempotency-Key"].ToString(),
            "D", out var idempotencyKey))
    {
        return Results.BadRequest(new { error = "A UUID Idempotency-Key header is required." });
    }

    var outcome = store.Create(name, idempotencyKey);
    if (outcome.Conflict)
    {
        return Results.Conflict(new { error = "This Idempotency-Key was used for a different request." });
    }

    return outcome.Created
        ? Results.Created($"/api/v1/workspaces/{outcome.Workspace.Id:D}", outcome.Workspace)
        : Results.Ok(outcome.Workspace);
}).RequireAuthorization();

// Repository registration records source locations but does not open, scan,
// resolve Git revision, parse, build or execute imported repository contents.
app.MapGet("/api/v1/workspaces/{workspaceId:guid}/repositories",
    (Guid workspaceId, WorkspaceStore store) =>
{
    if (store.Get(workspaceId) is null)
    {
        return Results.NotFound();
    }

    return Results.Ok(store.ListRepositories(workspaceId));
}).RequireAuthorization();

app.MapGet("/api/v1/workspaces/{workspaceId:guid}/repositories/{repositoryId:guid}",
    (Guid workspaceId, Guid repositoryId, WorkspaceStore store) =>
{
    var repository = store.GetRepository(workspaceId, repositoryId);
    return repository is null ? Results.NotFound() : Results.Ok(repository);
}).RequireAuthorization();

app.MapPost("/api/v1/workspaces/{workspaceId:guid}/repositories",
    (Guid workspaceId, RegisterRepositoryRequest request,
     HttpContext context, WorkspaceStore store) =>
{
    if (store.Get(workspaceId) is null)
    {
        return Results.NotFound(new { error = "Workspace does not exist." });
    }

    if (!Guid.TryParseExact(context.Request.Headers["Idempotency-Key"].ToString(),
            "D", out var idempotencyKey))
    {
        return Results.BadRequest(new { error = "A UUID Idempotency-Key header is required." });
    }

    var validated = RepositoryPathPolicy.Validate(request.LocalPath, store.DatabasePath);
    if (!validated.IsValid)
    {
        return Results.BadRequest(new { error = validated.Error });
    }

    var result = store.RegisterRepository(workspaceId, validated.Location!, idempotencyKey);
    return result.Status switch
    {
        RepositoryRegistrationStatus.Created =>
            Results.Created(
                $"/api/v1/workspaces/{workspaceId:D}/repositories/{result.Repository!.Id:D}",
                result.Repository),
        RepositoryRegistrationStatus.Replayed => Results.Ok(result.Repository),
        RepositoryRegistrationStatus.DuplicatePath =>
            Results.Conflict(new { error = "This local directory is already registered in the workspace." }),
        _ => Results.Conflict(
            new { error = "This Idempotency-Key was already used for another repository." })
    };
}).RequireAuthorization();

app.Run();

public sealed record LoginRequest(string? Code);
public partial class Program { }
