using System.Security.Cryptography;
using System.Text;

namespace CodeArcheologist.Api.Security;

/// <summary>
/// Process-scoped local pairing secret. This is not a multi-user identity system.
/// </summary>
public sealed class LocalAccessState
{
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(1);
    private readonly object _gate = new();
    private readonly byte[] _secret;
    private readonly Dictionary<string, DateTimeOffset> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _tokens = new(StringComparer.Ordinal);
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(30);
    private int _failedAttempts;
    private DateTimeOffset _lockedUntil;

    public LocalAccessState()
    {
        var supplied = Environment.GetEnvironmentVariable("CODE_ARCHEOLOGIST_BOOTSTRAP_CODE");
        if (supplied is not null && (supplied.Length != 32 ||
            !supplied.All(static c => char.IsAsciiHexDigit(c))))
        {
            throw new InvalidOperationException(
                "CODE_ARCHEOLOGIST_BOOTSTRAP_CODE must be 32 hexadecimal characters.");
        }

        BootstrapCode = supplied?.ToLowerInvariant()
            ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _secret = Encoding.ASCII.GetBytes(BootstrapCode);
        InstanceId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    }

    public string BootstrapCode { get; }
    public string InstanceId { get; }

    public string CreateSession()
    {
        lock (_gate)
        {
            ExpireSessions();
            if (_sessions.Count >= 128)
            {
                throw new InvalidOperationException("Too many active local sessions.");
            }

            var sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _sessions.Add(sessionId, DateTimeOffset.UtcNow.AddMinutes(30));
            return sessionId;
        }
    }

    public bool IsSessionActive(string? sessionId)
    {
        if (sessionId is null)
        {
            return false;
        }

        lock (_gate)
        {
            ExpireSessions();
            return _sessions.ContainsKey(sessionId);
        }
    }

    public void RevokeSession(string? sessionId)
    {
        if (sessionId is null) return;
        lock (_gate)
        {
            _sessions.Remove(sessionId);
        }
    }

    private void ExpireSessions()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _sessions.Where(entry => entry.Value <= now).ToArray())
        {
            _sessions.Remove(entry.Key);
        }
    }

    /// <summary>
    /// W1 bearer token: random 256-bit, in-memory only, bound to this process,
    /// fixed 30-minute lifetime, revocable. Never persisted or logged.
    /// Only the SHA-256 of the token is retained.
    /// </summary>
    public (string Token, DateTimeOffset ExpiresAtUtc) IssueToken()
    {
        lock (_gate)
        {
            ExpireTokens();
            if (_tokens.Count >= 128) throw new InvalidOperationException("Too many active access tokens.");
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var expires = DateTimeOffset.UtcNow.Add(TokenLifetime);
            _tokens.Add(Digest(token), expires);
            return (token, expires);
        }
    }

    public bool IsTokenActive(string? token)
    {
        if (token is not { Length: 64 }) return false;
        lock (_gate)
        {
            ExpireTokens();
            return _tokens.ContainsKey(Digest(token));
        }
    }

    public void RevokeToken(string? token)
    {
        if (token is null) return;
        lock (_gate) _tokens.Remove(Digest(token));
    }

    private static string Digest(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token)));

    private void ExpireTokens()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _tokens.Where(entry => entry.Value <= now).ToArray())
            _tokens.Remove(entry.Key);
    }

    public bool TryPair(string? candidate)
    {
        lock (_gate)
        {
            if (DateTimeOffset.UtcNow < _lockedUntil)
            {
                return false;
            }

            var value = candidate is { Length: 32 }
                ? Encoding.ASCII.GetBytes(candidate.ToLowerInvariant())
                : Array.Empty<byte>();

            if (CryptographicOperations.FixedTimeEquals(_secret, value))
            {
                _failedAttempts = 0;
                return true;
            }

            _failedAttempts++;
            if (_failedAttempts >= MaxFailedAttempts)
            {
                _failedAttempts = 0;
                _lockedUntil = DateTimeOffset.UtcNow.Add(LockoutDuration);
            }
            return false;
        }
    }
}
