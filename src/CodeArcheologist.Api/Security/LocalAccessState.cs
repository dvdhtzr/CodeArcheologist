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
