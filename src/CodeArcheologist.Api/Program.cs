using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using CodeArcheologist.Api.Security;

var builder = WebApplication.CreateBuilder(args);

// Security invariant: never listen on LAN or wildcard addresses by default.
builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(5127));

builder.Services.AddSingleton<LocalAccessState>();
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
            if (context.Principal?.FindFirst("local_process")?.Value != state.InstanceId)
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
    var identity = new ClaimsIdentity(
    [
        new Claim(ClaimTypes.Name, "local-owner"),
        new Claim("local_process", access.InstanceId),
        new Claim(LocalRequestGuard.CsrfClaimType, csrf)
    ], CookieAuthenticationDefaults.AuthenticationScheme);

    await context.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity));

    return Results.Ok(new { authenticated = true, csrfToken = csrf });
});

app.MapGet("/api/v1/auth/session", (ClaimsPrincipal user) =>
    Results.Ok(new
    {
        authenticated = true,
        csrfToken = user.FindFirst(LocalRequestGuard.CsrfClaimType)?.Value
    }))
    .RequireAuthorization();

app.MapPost("/api/v1/auth/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
}).RequireAuthorization();

app.Run();

public sealed record LoginRequest(string? Code);
public partial class Program { }
