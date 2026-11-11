using System.Security.Cryptography;
using System.Text;

namespace CodeArcheologist.Api.Security;

/// <summary>
/// Rejects DNS rebinding Host headers, unapproved browser origins and unsafe
/// browser requests without a custom header and a session-bound CSRF token.
/// This protects only the current fixed-port local host, not remote deployment.
/// </summary>
public sealed class LocalRequestGuard(RequestDelegate next)
{
    private const int ApiPort = 5127;
    private const string CsrfClaim = "local_csrf";

    private static readonly HashSet<string> LocalHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost", "127.0.0.1", "::1"
    };

    private static readonly HashSet<string> LocalOrigins = new(StringComparer.OrdinalIgnoreCase)
    {
        "http://localhost:5127",
        "http://127.0.0.1:5127",
        "http://[::1]:5127"
    };

    private static readonly HashSet<string> DeveloperOrigins = new(StringComparer.OrdinalIgnoreCase)
    {
        "http://localhost:5174",
        "http://127.0.0.1:5174",
        "http://localhost:5175",
        "http://127.0.0.1:5175"
    };

    public async Task InvokeAsync(HttpContext context, ClientOriginPolicy clientOrigins)
    {
        var host = context.Request.Host;
        if (host.Port != ApiPort || !LocalHosts.Contains(host.Host))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Cache-Control"] = "no-store";

        var requestOrigin = context.Request.Headers["Origin"].ToString();
        bool OriginAllowed(string o) => LocalOrigins.Contains(o) || clientOrigins.Allows(o) ||
            (Environment.GetEnvironmentVariable("CODE_ARCHEOLOGIST_DEV_UI") == "1" && DeveloperOrigins.Contains(o));

        // W1: the SignalR hub (including its WebSocket upgrade GET) accepts
        // browser connections only from allowed origins; it is never anonymous.
        if (context.Request.Path.StartsWithSegments(LocalBearerHandler.HubPathPrefix) &&
            requestOrigin.Length > 0 && !OriginAllowed(requestOrigin))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (!context.Request.Path.StartsWithSegments("/api") ||
            HttpMethods.IsGet(context.Request.Method) ||
            HttpMethods.IsHead(context.Request.Method) ||
            HttpMethods.IsOptions(context.Request.Method))
        {
            await next(context);
            return;
        }

        var fetchSite = context.Request.Headers["Sec-Fetch-Site"].ToString();
        if (string.Equals(fetchSite, "cross-site", StringComparison.OrdinalIgnoreCase) &&
            !clientOrigins.Allows(requestOrigin))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var origin = requestOrigin;
        var allowedOrigin = OriginAllowed(origin);

        if (origin.Length > 0 && !allowedOrigin)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // Disallow browser-reachable unsafe requests without a validated Origin.
        // Non-browser CLI clients can omit Origin and Sec-Fetch-Site but still need
        // the custom header, pairing code and (once authenticated) CSRF token.
        if (origin.Length == 0 && fetchSite.Length > 0)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // Bearer-authenticated requests carry no ambient credential, so they
        // need neither the custom header nor the cookie-bound CSRF token. The
        // Origin allowlist above, idempotency keys and concurrency still apply.
        if (context.User.FindFirst(LocalBearerHandler.AuthMethodClaim)?.Value == LocalBearerHandler.SchemeName)
        {
            await next(context);
            return;
        }

        if (context.Request.Headers["X-CodeArcheologist-Request"].ToString() != "1")
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // Pairing is the one unauthenticated mutation and requires an explicit
        // 128-bit bootstrap code. No cookies are accepted as pairing authority.
        if ((string.Equals(context.Request.Path.Value, "/api/v1/auth/login", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(context.Request.Path.Value, "/api/v1/auth/token", StringComparison.OrdinalIgnoreCase)) &&
            HttpMethods.IsPost(context.Request.Method))
        {
            await next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var expected = context.User.FindFirst(CsrfClaim)?.Value;
        var supplied = context.Request.Headers["X-CodeArcheologist-Csrf"].ToString();

        if (expected is null || expected.Length != supplied.Length ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected),
                Encoding.ASCII.GetBytes(supplied)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await next(context);
    }

    public const string CsrfClaimType = CsrfClaim;
}
