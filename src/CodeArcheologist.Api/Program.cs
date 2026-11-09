using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using CodeArcheologist.Api.Security;
using CodeArcheologist.Api.Workspaces;
using CodeArcheologist.Api.Investigations;

var builder = WebApplication.CreateBuilder(args);
// The developer scripts run from the monorepo root. Resolve the packaged
// local model selection independently of the process working directory.
builder.Configuration.AddJsonFile(
    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
    optional: true, reloadOnChange: false);

// Security invariant: never listen on LAN or wildcard addresses by default.
builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(5127));

builder.Services.AddSingleton<LocalAccessState>();
builder.Services.AddSingleton<WorkspaceStore>();
builder.Services.AddSingleton<SnapshotCaptureService>();
builder.Services.AddSingleton<CSharpSyntaxInspector>();
builder.Services.AddSingleton<CSharpInvocationSyntaxInspector>();
builder.Services.AddSingleton<CSharpProvisionalInvocationBinder>();
builder.Services.AddSingleton<CSharpHttpAttributeInspector>();
builder.Services.AddSingleton<CSharpMinimalApiInspector>();
builder.Services.AddSingleton<CSharpDiRegistrationInspector>();
builder.Services.AddSingleton<CSharpTypeRelationshipInspector>();
builder.Services.AddSingleton<StaticCSharpProjectInspector>();
builder.Services.AddSingleton<CSharpProjectLinkContextInspector>();
builder.Services.AddSingleton<CSharpEvidenceGraphBuilder>();
builder.Services.AddSingleton<SnapshotEvidenceTools>();
builder.Services.AddSingleton<LocalMafInvestigation>();
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

// Explicit read-only acquisition of a registered local folder. Files are
// staged under app data, never executed or modified at their original path.
// This is not a Git commit resolver or a semantic analyzer.
app.MapGet("/api/v1/workspaces/{workspaceId:guid}/repositories/{repositoryId:guid}/snapshots",
    (Guid workspaceId, Guid repositoryId, WorkspaceStore store) =>
{
    if (store.GetRepository(workspaceId, repositoryId) is null)
        return Results.NotFound();

    return Results.Ok(store.ListSnapshots(workspaceId, repositoryId));
}).RequireAuthorization();

app.MapGet("/api/v1/workspaces/{workspaceId:guid}/repositories/{repositoryId:guid}/snapshots/{snapshotId:guid}",
    (Guid workspaceId, Guid repositoryId, Guid snapshotId, WorkspaceStore store) =>
{
    var snapshot = store.GetSnapshot(workspaceId, repositoryId, snapshotId);
    return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
}).RequireAuthorization();

app.MapGet("/api/v1/workspaces/{workspaceId:guid}/repositories/{repositoryId:guid}/snapshots/{snapshotId:guid}/manifest",
    (Guid workspaceId, Guid repositoryId, Guid snapshotId,
     WorkspaceStore store, SnapshotCaptureService service) =>
{
    var snapshot = store.GetSnapshot(workspaceId, repositoryId, snapshotId);
    if (snapshot is null) return Results.NotFound();

    try
    {
        return Results.Ok(service.ReadManifest(snapshot));
    }
    catch (SnapshotCaptureException error)
    {
        return Results.Json(new { error = error.Message }, statusCode: error.StatusCode);
    }
    catch (IOException)
    {
        return Results.Conflict(new { error = "Snapshot manifest cannot be read." });
    }
}).RequireAuthorization();

// Deterministic filename-only inventory of a previously captured snapshot.
// The source manifest AND saved bytes are integrity-checked first; this route
// never follows the mutable registered repository path or executes source.
app.MapGet("/api/v1/workspaces/{workspaceId:guid}/repositories/{repositoryId:guid}/snapshots/{snapshotId:guid}/inventory",
    (Guid workspaceId, Guid repositoryId, Guid snapshotId,
     WorkspaceStore store, SnapshotCaptureService service) =>
{
    var snapshot = store.GetSnapshot(workspaceId, repositoryId, snapshotId);
    if (snapshot is null) return Results.NotFound();

    try
    {
        var manifest = service.ReadManifest(snapshot);
        return Results.Ok(SourceInventoryBuilder.Build(manifest));
    }
    catch (SnapshotCaptureException error)
    {
        return Results.Json(new { error = error.Message }, statusCode: error.StatusCode);
    }
    catch (IOException)
    {
        return Results.Conflict(new { error = "Snapshot inventory could not be verified." });
    }
}).RequireAuthorization();

// Roslyn syntax-only inspection over verified, copied source bytes. This
// does NOT evaluate projects, compile code, resolve references or execute
// build targets/source generators/analyzers from imported repositories.
app.MapGet("/api/v1/workspaces/{workspaceId:guid}/repositories/{repositoryId:guid}/snapshots/{snapshotId:guid}/csharp-syntax",
    (Guid workspaceId, Guid repositoryId, Guid snapshotId,
     WorkspaceStore store, CSharpSyntaxInspector inspector, CancellationToken cancellationToken) =>
{
    var snapshot = store.GetSnapshot(workspaceId, repositoryId, snapshotId);
    if (snapshot is null) return Results.NotFound();

    try
    {
        return Results.Ok(inspector.Inspect(snapshot, cancellationToken));
    }
    catch (SnapshotCaptureException error)
    {
        return Results.Json(new { error = error.Message }, statusCode: error.StatusCode);
    }
    catch (IOException)
    {
        return Results.Conflict(new { error = "Saved C# source could not be verified." });
    }
}).RequireAuthorization();

// Source-local, provisional C# type relationships. This intentionally uses
// a reference-free synthetic Roslyn compilation; it does not load project
// files, resolve external dependencies, execute code or claim actual project
// build or runtime edges.
app.MapGet("/api/v1/workspaces/{workspaceId:guid}/repositories/{repositoryId:guid}/snapshots/{snapshotId:guid}/csharp-type-links",
    (Guid workspaceId, Guid repositoryId, Guid snapshotId, WorkspaceStore store,
     CSharpTypeRelationshipInspector inspector, CancellationToken cancellationToken) =>
{
    var snapshot = store.GetSnapshot(workspaceId, repositoryId, snapshotId);
    if (snapshot is null) return Results.NotFound();

    try
    {
        return Results.Ok(inspector.Inspect(snapshot, cancellationToken));
    }
    catch (SnapshotCaptureException error)
    {
        return Results.Json(new { error = error.Message }, statusCode: error.StatusCode);
    }
    catch (IOException)
    {
        return Results.Conflict(new { error = "Saved C# source could not be verified." });
    }
}).RequireAuthorization();

// Static, project-file-scoped source membership candidates. Never evaluates
// MSBuild, imports, targets, props, scripts, restore or imported binaries.
app.MapGet("/api/v1/workspaces/{workspaceId:guid}/repositories/{repositoryId:guid}/snapshots/{snapshotId:guid}/csharp-projects",
    (Guid workspaceId, Guid repositoryId, Guid snapshotId, WorkspaceStore store,
     StaticCSharpProjectInspector inspector, CancellationToken cancellationToken) =>
{
    var snapshot = store.GetSnapshot(workspaceId, repositoryId, snapshotId);
    if (snapshot is null) return Results.NotFound();

    try
    {
        return Results.Ok(inspector.Inspect(snapshot, cancellationToken));
    }
    catch (SnapshotCaptureException error)
    {
        return Results.Json(new { error = error.Message }, statusCode: error.StatusCode);
    }
    catch (IOException)
    {
        return Results.Conflict(new { error = "Saved project XML could not be verified." });
    }
}).RequireAuthorization();

// Evidence-joined, candidate-only project context for source-local Roslyn
// type usages. No imported code or MSBuild evaluation is performed.
app.MapGet("/api/v1/workspaces/{workspaceId:guid}/repositories/{repositoryId:guid}/snapshots/{snapshotId:guid}/csharp-link-context",
    (Guid workspaceId, Guid repositoryId, Guid snapshotId, WorkspaceStore store,
     CSharpProjectLinkContextInspector inspector, CancellationToken cancellationToken) =>
{
    var snapshot = store.GetSnapshot(workspaceId, repositoryId, snapshotId);
    if (snapshot is null) return Results.NotFound();

    try
    {
        return Results.Ok(inspector.Inspect(snapshot, cancellationToken));
    }
    catch (SnapshotCaptureException error)
    {
        return Results.Json(new { error = error.Message }, statusCode: error.StatusCode);
    }
    catch (IOException)
    {
        return Results.Conflict(new { error = "Saved source evidence could not be verified." });
    }
}).RequireAuthorization();

// Read-only, snapshot-grounded graph projection. All semantic edges remain
// candidate/provisional; this is not a verified project dependency graph.
app.MapGet("/api/v1/workspaces/{workspaceId:guid}/repositories/{repositoryId:guid}/snapshots/{snapshotId:guid}/csharp-evidence-graph",
    (Guid workspaceId, Guid repositoryId, Guid snapshotId, WorkspaceStore store,
     CSharpEvidenceGraphBuilder builder, CancellationToken cancellationToken) =>
{
    var snapshot = store.GetSnapshot(workspaceId, repositoryId, snapshotId);
    if (snapshot is null) return Results.NotFound();

    try
    {
        return Results.Ok(builder.Build(snapshot, cancellationToken));
    }
    catch (SnapshotCaptureException error)
    {
        return Results.Json(new { error = error.Message }, statusCode: error.StatusCode);
    }
    catch (IOException)
    {
        return Results.Conflict(new { error = "Saved graph evidence could not be verified." });
    }
}).RequireAuthorization();

// Agent-ready evidence tools use the SAME authenticated immutable snapshot
// identities and verified Roslyn source graph. They are deterministic and do
// not run agents, imported source, model providers or arbitrary file reads.
app.MapGet("/api/v1/workspaces/{workspaceId:guid}/repositories/{repositoryId:guid}/snapshots/{snapshotId:guid}/evidence-tools/entry-point",
    (Guid workspaceId, Guid repositoryId, Guid snapshotId,
        string? entryPointId, WorkspaceStore store,
        SnapshotEvidenceTools tools, CancellationToken cancellationToken) =>
{
    var snapshot = store.GetSnapshot(workspaceId, repositoryId, snapshotId);
    if (snapshot is null) return Results.NotFound();
    try
    {
        return Results.Ok(tools.ReadEntryPoint(snapshot, entryPointId, cancellationToken));
    }
    catch (SnapshotCaptureException error)
    {
        return Results.Json(new { error = error.Message }, statusCode: error.StatusCode);
    }
    catch (IOException)
    {
        return Results.Conflict(new { error = "Saved evidence tool data could not be verified." });
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Json(new { error = "Saved evidence is not accessible." }, statusCode: 422);
    }
}).RequireAuthorization();

app.MapPost("/api/v1/workspaces/{workspaceId:guid}/repositories/{repositoryId:guid}/snapshots/{snapshotId:guid}/evidence-tools/check-citations",
    (Guid workspaceId, Guid repositoryId, Guid snapshotId,
        AgentCitationCheckRequest request, WorkspaceStore store,
        SnapshotEvidenceTools tools, CancellationToken cancellationToken) =>
{
    var snapshot = store.GetSnapshot(workspaceId, repositoryId, snapshotId);
    if (snapshot is null) return Results.NotFound();
    try
    {
        return Results.Ok(tools.CheckCitations(snapshot, request, cancellationToken));
    }
    catch (SnapshotCaptureException error)
    {
        return Results.Json(new { error = error.Message }, statusCode: error.StatusCode);
    }
    catch (IOException)
    {
        return Results.Conflict(new { error = "Saved citation evidence could not be verified." });
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Json(new { error = "Saved citation evidence is not accessible." }, statusCode: 422);
    }
}).RequireAuthorization();

// Explicit user-triggered, bounded local MAF investigation. No automatic
// analysis or LLM request when opening a workspace or reading a snapshot.
app.MapPost("/api/v1/workspaces/{workspaceId:guid}/repositories/{repositoryId:guid}/snapshots/{snapshotId:guid}/investigations/local-maf",
    async (Guid workspaceId, Guid repositoryId, Guid snapshotId,
        LocalMafRequest request, WorkspaceStore store,
        LocalMafInvestigation investigator, CancellationToken cancellationToken) =>
{
    var snapshot = store.GetSnapshot(workspaceId, repositoryId, snapshotId);
    if (snapshot is null) return Results.NotFound();
    try
    {
        return Results.Ok(await investigator.RunAsync(
            snapshot, request.EntryPointId, cancellationToken));
    }
    catch (SnapshotCaptureException error)
    {
        return Results.Json(new { error = error.Message }, statusCode: error.StatusCode);
    }
    catch (IOException)
    {
        return Results.Conflict(new { error = "Local agent evidence could not be verified." });
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Json(new { error = "Local agent evidence is not accessible." }, statusCode: 422);
    }
}).RequireAuthorization();

app.MapPost("/api/v1/workspaces/{workspaceId:guid}/repositories/{repositoryId:guid}/snapshots",
    async (Guid workspaceId, Guid repositoryId, HttpContext context,
        WorkspaceStore store, SnapshotCaptureService service, CancellationToken cancellationToken) =>
{
    if (store.GetRepository(workspaceId, repositoryId) is null)
        return Results.NotFound();

    if (!Guid.TryParseExact(context.Request.Headers["Idempotency-Key"].ToString(),
            "D", out var idempotencyKey))
    {
        return Results.BadRequest(new { error = "A UUID Idempotency-Key header is required." });
    }

    try
    {
        var outcome = await service.CaptureAsync(
            workspaceId, repositoryId, idempotencyKey, cancellationToken);
        var uri = $"/api/v1/workspaces/{workspaceId:D}/repositories/{repositoryId:D}/snapshots/{outcome.Snapshot.Id:D}";
        return outcome.Created
            ? Results.Created(uri, outcome.Snapshot)
            : Results.Ok(outcome.Snapshot);
    }
    catch (SnapshotCaptureException error)
    {
        return Results.Json(new { error = error.Message }, statusCode: error.StatusCode);
    }
    catch (IOException)
    {
        return Results.Conflict(new { error = "Repository changed or could not be read safely." });
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Json(new { error = "Access to the source was denied." }, statusCode: 422);
    }
}).RequireAuthorization();

app.Run();

public sealed record LoginRequest(string? Code);
public partial class Program { }
