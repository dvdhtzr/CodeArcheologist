using Microsoft.AspNetCore.Cors.Infrastructure;

namespace CodeArcheologist.Api.Security;

/// <summary>
/// CORS for white-label clients: only the explicitly configured loopback
/// origins; exact headers and methods. Credentials are allowed only for
/// those exact origins (the SignalR browser client negotiates with
/// credentials by default); cookies remain SameSite=Strict and CSRF-bound.
/// </summary>
public sealed class ClientCorsPolicyProvider(ClientOriginPolicy origins) : ICorsPolicyProvider
{
    private readonly CorsPolicy _policy = Build(origins);

    public Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName) =>
        Task.FromResult<CorsPolicy?>(_policy);

    private static CorsPolicy Build(ClientOriginPolicy origins)
    {
        var builder = new CorsPolicyBuilder()
            .WithHeaders("Authorization", "Content-Type", "Idempotency-Key", "X-CodeArcheologist-Request",
                "X-Requested-With", "X-SignalR-User-Agent")
            .WithMethods("GET", "POST")
            .WithExposedHeaders("Location");
        if (origins.Origins.Count == 0) return builder.Build(); // no origin is allowed
        return builder.WithOrigins(origins.Origins.ToArray()).AllowCredentials().Build();
    }
}
