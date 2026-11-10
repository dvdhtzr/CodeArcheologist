using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace CodeArcheologist.Api.Security;

/// <summary>
/// W1 white-label authentication: the opaque, process-bound bearer token
/// issued by POST /api/v1/auth/token. REST clients send
/// "Authorization: Bearer &lt;token&gt;". Browser WebSocket clients cannot set
/// headers, so ONLY on the SignalR hub path is the token also accepted as the
/// access_token query parameter (the standard SignalR convention).
/// </summary>
public sealed class LocalBearerHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, LocalAccessState access)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "LocalBearer";
    public const string HubPathPrefix = "/hubs";

    public static string? ReadToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.Ordinal)) return header["Bearer ".Length..].Trim();
        if (request.Path.StartsWithSegments(HubPathPrefix) &&
            request.Query.TryGetValue("access_token", out var query) && query.Count == 1)
            return query[0];
        return null;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = ReadToken(Request);
        if (token is null) return Task.FromResult(AuthenticateResult.NoResult());
        if (!access.IsTokenActive(token)) return Task.FromResult(AuthenticateResult.Fail("invalid-or-expired-token"));
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "local-owner"),
            new Claim("local_process", access.InstanceId),
            new Claim(AuthMethodClaim, SchemeName)
        ], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    /// <summary>Marks principals authenticated by bearer token (no ambient credential, no CSRF).</summary>
    public const string AuthMethodClaim = "local_auth_method";
}
