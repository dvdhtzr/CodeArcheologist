var builder = WebApplication.CreateBuilder(args);

// Developer-only shell: no LAN/wildcard binding or mutable API routes.
builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(5127));

var app = builder.Build();

// This is only non-sensitive process-health information, not agent activity.
app.MapGet("/api/v1/health", () =>
    Results.Ok(new { status = "ok", component = "api", stage = "foundation" }))
    .WithName("GetHealth");

app.Run();

public partial class Program { }
