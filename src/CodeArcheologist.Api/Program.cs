using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Cors.Infrastructure;
using CodeArcheologist.Api.Security;
using CodeArcheologist.Api.Workspaces;
using CodeArcheologist.Api.Investigations;
using CodeArcheologist.Api.Execution;
using CodeArcheologist.Api.Realtime;
using CodeArcheologist.Api.Capabilities;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddSingleton<GateBInvestigator>();
builder.Services.AddSingleton<GateCEvidenceDiscovery>();
builder.Services.AddSingleton<GateC3IndependentRunner>();
builder.Services.AddSingleton<GateC4ReconciliationRunner>();
// C6b: admission service + ONE managed hosted worker. No detached tasks.
builder.Services.AddSingleton<ExecutionOwnershipState>();
builder.Services.AddSingleton<InvestigationWorkSignal>();
builder.Services.AddSingleton(InvestigationWorkerOptions.Default);
builder.Services.AddSingleton<IGateCEvidenceSessionFactory>(services =>
    services.GetRequiredService<GateCEvidenceDiscovery>());
builder.Services.AddSingleton<IGateCRoleStageRunner, MafRoleStageRunner>();
builder.Services.AddSingleton<GateC6CheckpointRevalidator>();
builder.Services.AddSingleton<IInvestigationExecutor, GateC6StageCoordinator>();
builder.Services.AddSingleton<InvestigationContinuationService>();
builder.Services.AddSingleton<InvestigationCancellationService>();
builder.Services.AddSingleton<InvestigationExecutionProjectionService>();
builder.Services.AddSingleton<InvestigationAdmissionService>();
builder.Services.AddSingleton<InvestigationWorker>();
builder.Services.AddHostedService(services => services.GetRequiredService<InvestigationWorker>());
// W1 white-label API: explicit loopback client-origin allowlist, push-only
// SignalR hub, OpenAPI contract. The API itself still listens on loopback only.
builder.Services.AddSingleton(services =>
    new ClientOriginPolicy(services.GetRequiredService<WorkspaceStore>().DataDirectory));
builder.Services.AddCors();
builder.Services.AddSingleton<ICorsPolicyProvider, ClientCorsPolicyProvider>();
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = false; // only fixed HubException codes reach clients
    options.MaximumReceiveMessageSize = 4096;
});
builder.Services.AddSingleton<RealtimeSubscriptions>();
builder.Services.AddHostedService<RealtimePublisher>();
builder.Services.AddOpenApi("v1");

// Cookie (bundled client, CSRF-protected) or process-bound bearer token
// (white-label clients); the policy scheme picks per request.
builder.Services.AddAuthentication(options => options.DefaultScheme = "Local")
    .AddPolicyScheme("Local", "Local", options => options.ForwardDefaultSelector = context =>
        LocalBearerHandler.ReadToken(context.Request) is not null
            ? LocalBearerHandler.SchemeName : CookieAuthenticationDefaults.AuthenticationScheme)
    .AddScheme<AuthenticationSchemeOptions, LocalBearerHandler>(LocalBearerHandler.SchemeName, _ => { })
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
// C6b startup protocol: (1) migrate only — never recover here; (2) try the
// OS execution lock; (3) without it: no execution and no recovery; (4–7)
// with it: new fenced epoch, reconcile abandoned attempts, preserve
// checkpoints, then idle until work is explicitly admitted.
var workspaceStore = app.Services.GetRequiredService<WorkspaceStore>();
workspaceStore.Initialize();
var executionOwnership = app.Services.GetRequiredService<ExecutionOwnershipState>();
if (!executionOwnership.TryAcquire(workspaceStore))
    Console.WriteLine($"Investigation execution disabled in this process: {executionOwnership.Diagnostic}.");

var localAccess = app.Services.GetRequiredService<LocalAccessState>();
app.Lifetime.ApplicationStarted.Register(() =>
    Console.WriteLine($"Code Archeologist local pairing code: {localAccess.BootstrapCode}"));

app.UseCors();
app.UseAuthentication();
app.UseMiddleware<LocalRequestGuard>();
app.UseAuthorization();

// Versioned contracts for white-label front ends (schema only; no data).
app.MapOpenApi("/api/{documentName}/openapi.json");
app.MapHub<InvestigationEventsHub>(RealtimeContract.HubPath);
// W2: read-only capability resources (entry points, methods, one-hop impact).
app.MapSnapshotCapabilities();

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

// W1: exchange the console pairing code for a short-lived, process-bound
// bearer token for white-label clients (REST and SignalR). Same lockout as login.
app.MapPost("/api/v1/auth/token", (LoginRequest request, LocalAccessState access) =>
{
    if (!access.TryPair(request.Code)) return Results.Unauthorized();
    var (token, expires) = access.IssueToken();
    return Results.Ok(new { tokenType = "Bearer", accessToken = token, expiresAtUtc = expires });
});

app.MapPost("/api/v1/auth/token/revoke", (HttpContext context, LocalAccessState access) =>
{
    access.RevokeToken(LocalBearerHandler.ReadToken(context.Request));
    return Results.NoContent();
}).RequireAuthorization();

app.MapPost("/api/v1/auth/logout", async (HttpContext context,
    LocalAccessState access) =>
{
    access.RevokeSession(context.User.FindFirst("local_session")?.Value);
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
}).RequireAuthorization();


 // C5a: report infrastructure only. Every request is scoped to registered
 // repository and saved snapshot identities; there is no imported-code execution.
app.MapGet("/api/v1/workspaces/{workspaceId:guid}/repositories/{repositoryId:guid}/snapshots/{snapshotId:guid}/investigations",
    (Guid workspaceId, Guid repositoryId, Guid snapshotId, WorkspaceStore store) =>
{
    if (store.GetSnapshot(workspaceId, repositoryId, snapshotId) is null)
        return Results.NotFound();
    return Results.Ok(store.ListInvestigations(workspaceId, repositoryId, snapshotId));
}).RequireAuthorization();

app.MapGet("/api/v1/workspaces/{workspaceId:guid}/repositories/{repositoryId:guid}/snapshots/{snapshotId:guid}/investigations/{runId:guid}",
    (Guid workspaceId, Guid repositoryId, Guid snapshotId, Guid runId,
     WorkspaceStore store) =>
{
    if (store.GetSnapshot(workspaceId, repositoryId, snapshotId) is null)
        return Results.NotFound();
    var detail = store.GetInvestigation(workspaceId, repositoryId, snapshotId, runId);
    return detail is null ? Results.NotFound() : Results.Ok(detail);
}).RequireAuthorization();

// C6a: read-only, ordered host event history scoped to the requested snapshot.
app.MapGet("/api/v1/workspaces/{workspaceId:guid}/repositories/{repositoryId:guid}/snapshots/{snapshotId:guid}/investigations/{runId:guid}/events",
    (Guid workspaceId, Guid repositoryId, Guid snapshotId, Guid runId,
     WorkspaceStore store) =>
{
    if (store.GetInvestigation(workspaceId, repositoryId, snapshotId, runId) is null)
        return Results.NotFound();
    return Results.Ok(store.ListEvents(workspaceId, repositoryId, snapshotId, runId));
}).RequireAuthorization();

// C6b: versioned, read-only, scoped execution projection (attempts, host-observed
// stages, checkpoint indicators, recovery eligibility, safe diagnostics).
app.MapGet("/api/v1/workspaces/{workspaceId:guid}/repositories/{repositoryId:guid}/snapshots/{snapshotId:guid}/investigations/{runId:guid}/execution",
    (Guid workspaceId, Guid repositoryId, Guid snapshotId, Guid runId,
     InvestigationExecutionProjectionService projections, CancellationToken cancellationToken) =>
{
    var projection = projections.Project(workspaceId, repositoryId, snapshotId, runId, cancellationToken);
    return projection is null ? Results.NotFound() : Results.Ok(projection);
}).RequireAuthorization();

// C6b: records durable cancellation INTENT (authenticated, CSRF-guarded,
// idempotent, optimistic on expectedAttemptNumber). The worker commits the
// terminal cancellation only after the in-flight model call returns.
app.MapPost("/api/v1/workspaces/{workspaceId:guid}/repositories/{repositoryId:guid}/snapshots/{snapshotId:guid}/investigations/{runId:guid}/cancel",
    (Guid workspaceId, Guid repositoryId, Guid snapshotId, Guid runId,
     GateC6ContinueRequest request, HttpContext context,
     InvestigationCancellationService cancellations) =>
{
    if (!Guid.TryParseExact(context.Request.Headers["Idempotency-Key"].ToString(),
            "D", out var idempotencyKey) || idempotencyKey == Guid.Empty)
        return Results.BadRequest(new { error = "A nonempty UUID Idempotency-Key is required." });
    if (request.ExpectedAttemptNumber is not int expected || expected < 1)
        return Results.BadRequest(new { error = "expectedAttemptNumber is required." });
    var result = cancellations.Cancel(workspaceId, repositoryId, snapshotId, runId,
        idempotencyKey, expected);
    var body = new { diagnostic = result.Diagnostic, attempt = result.Attempt };
    return result.Outcome switch
    {
        CancellationOutcome.Requested => Results.Accepted(
            $"/api/v1/workspaces/{workspaceId:D}/repositories/{repositoryId:D}/snapshots/{snapshotId:D}/investigations/{runId:D}/execution",
            body),
        CancellationOutcome.Replayed => Results.Ok(body),
        CancellationOutcome.NotFound => Results.NotFound(),
        CancellationOutcome.ExecutionUnavailable => Results.Json(body, statusCode: 503),
        _ => Results.Conflict(body)
    };
}).RequireAuthorization();

// C6b: explicit, authenticated, CSRF-protected (LocalRequestGuard) and
// idempotent continuation. Requires the expected latest attempt number
// (optimistic concurrency). Revalidates the snapshot and every accepted
// checkpoint before admitting a new attempt; never runs a model in-request.
app.MapPost("/api/v1/workspaces/{workspaceId:guid}/repositories/{repositoryId:guid}/snapshots/{snapshotId:guid}/investigations/{runId:guid}/continue",
    (Guid workspaceId, Guid repositoryId, Guid snapshotId, Guid runId,
     GateC6ContinueRequest request, HttpContext context,
     InvestigationContinuationService continuations) =>
{
    if (!Guid.TryParseExact(context.Request.Headers["Idempotency-Key"].ToString(),
            "D", out var idempotencyKey) || idempotencyKey == Guid.Empty)
        return Results.BadRequest(new { error = "A nonempty UUID Idempotency-Key is required." });
    if (request.ExpectedAttemptNumber is not int expected || expected < 1)
        return Results.BadRequest(new { error = "expectedAttemptNumber is required." });
    var result = continuations.Continue(workspaceId, repositoryId, snapshotId, runId,
        idempotencyKey, expected, context.RequestAborted);
    var body = new { diagnostic = result.Diagnostic, attempt = result.Attempt };
    return result.Outcome switch
    {
        ContinuationOutcome.Admitted => Results.Accepted(
            $"/api/v1/workspaces/{workspaceId:D}/repositories/{repositoryId:D}/snapshots/{snapshotId:D}/investigations/{runId:D}",
            body),
        ContinuationOutcome.Replayed => Results.Ok(body),
        ContinuationOutcome.NotFound => Results.NotFound(),
        ContinuationOutcome.CheckpointRejected => Results.Json(body, statusCode: 422),
        ContinuationOutcome.ExecutionUnavailable => Results.Json(body, statusCode: 503),
        _ => Results.Conflict(body)
    };
}).RequireAuthorization();

app.MapPost("/api/v1/workspaces/{workspaceId:guid}/repositories/{repositoryId:guid}/snapshots/{snapshotId:guid}/investigations",
    (Guid workspaceId, Guid repositoryId, Guid snapshotId,
     GateC5CreateRequest request, HttpContext context, WorkspaceStore store,
     SnapshotEvidenceTools tools, InvestigationAdmissionService admissions) =>
{
    var snapshot = store.GetSnapshot(workspaceId, repositoryId, snapshotId);
    if (snapshot is null) return Results.NotFound();
    if (!Guid.TryParseExact(context.Request.Headers["Idempotency-Key"].ToString(),
            "D", out var idempotencyKey) || idempotencyKey == Guid.Empty)
        return Results.BadRequest(new { error = "A nonempty UUID Idempotency-Key is required." });
    if (string.IsNullOrWhiteSpace(request.EntryPointId) ||
        request.EntryPointId.Length > 1024 ||
        request.EntryPointId.Any(char.IsControl))
        return Results.BadRequest(new { error = "A bounded entry-point ID is required." });
    // Authoritative scope comes from the store, never from model or browser.
    try
    {
        tools.ReadEntryPoint(snapshot, request.EntryPointId, context.RequestAborted);
    }
    catch (SnapshotCaptureException error)
    {
        return Results.Json(new { error = "Entry-point evidence unavailable or invalid." },
            statusCode: error.StatusCode);
    }
    catch (OperationCanceledException)
    {
        return Results.StatusCode(408);
    }
    catch (IOException)
    {
        return Results.Conflict(new { error = "Saved snapshot evidence is unavailable." });
    }

    var scope = new GateCScope(workspaceId, repositoryId, snapshotId,
        snapshot.FingerprintSha256, request.EntryPointId, SnapshotEvidenceTools.Version);
    var admission = admissions.Admit(scope, idempotencyKey);
    var location = admission.Record is null ? null :
        $"/api/v1/workspaces/{workspaceId:D}/repositories/{repositoryId:D}/snapshots/{snapshotId:D}/investigations/{admission.Record.RunId:D}";
    return admission.Outcome switch
    {
        InvestigationAdmissionOutcome.Admitted => Results.Accepted(location, admission.Record),
        InvestigationAdmissionOutcome.Replayed => Results.Ok(admission.Record),
        InvestigationAdmissionOutcome.Conflict =>
            Results.Conflict(new { error = "Idempotency-Key already belongs to another request." }),
        InvestigationAdmissionOutcome.Busy =>
            Results.Conflict(new { error = "A local investigation is already running." }),
        _ => Results.Json(new { error = "Investigation execution is unavailable in this process." },
            statusCode: StatusCodes.Status503ServiceUnavailable)
    };
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

// Gate B: explicit request for a single real local MAF investigator. The
// application resolves and verifies the exact saved snapshot before model use.
// The request has no model-supplied path, command, tool, or arbitrary prompt.
app.MapPost("/api/v1/workspaces/{workspaceId:guid}/repositories/{repositoryId:guid}/snapshots/{snapshotId:guid}/investigations/gate-b",
    async (Guid workspaceId, Guid repositoryId, Guid snapshotId,
        GateBInvestigationRequest request, WorkspaceStore store,
        GateBInvestigator investigator, CancellationToken cancellationToken) =>
{
    var snapshot = store.GetSnapshot(workspaceId, repositoryId, snapshotId);
    if (snapshot is null) return Results.NotFound();
    try
    {
        var result = await investigator.InvestigateAsync(
            snapshot, request.EntryPointId, cancellationToken);
        return Results.Ok(result);
    }
    catch (SnapshotCaptureException error)
    {
        return Results.Json(new { error = error.Message }, statusCode: error.StatusCode);
    }
    catch (IOException)
    {
        return Results.Conflict(new { error = "Saved investigator evidence could not be verified." });
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Json(new { error = "Saved investigator evidence is not accessible." }, statusCode: 422);
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
public sealed record GateBInvestigationRequest(string? EntryPointId);
public sealed record GateC5CreateRequest(string? EntryPointId);
public sealed record GateC6ContinueRequest(int? ExpectedAttemptNumber);
public partial class Program { }
