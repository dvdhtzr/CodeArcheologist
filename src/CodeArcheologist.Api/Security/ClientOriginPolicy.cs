using System.Text.Json;

namespace CodeArcheologist.Api.Security;

/// <summary>
/// W1 white-label boundary: the API stays loopback-only, and browser front
/// ends may call it only from origins the owner lists explicitly. Every
/// allowed origin must itself be a loopback http(s) origin with an explicit
/// port: no wildcards, paths, credentials or remote hosts.
///
/// Sources, in order: CODE_ARCHEOLOGIST_CLIENT_ORIGINS (comma-separated, for
/// disposable test processes) or &lt;data dir&gt;/client-origins.json
/// ({"schemaVersion":"code-archeologist-client-origins-v1","origins":[...]}).
/// An invalid configuration stops startup rather than widening access.
/// </summary>
public sealed class ClientOriginPolicy
{
    public const string FileName = "client-origins.json";
    public const string SchemaVersion = "code-archeologist-client-origins-v1";
    private static readonly HashSet<string> LoopbackHosts = new(StringComparer.OrdinalIgnoreCase)
        { "localhost", "127.0.0.1", "[::1]" };

    public ClientOriginPolicy(string dataDirectory)
    {
        var configured = Environment.GetEnvironmentVariable("CODE_ARCHEOLOGIST_CLIENT_ORIGINS");
        IEnumerable<string> raw = [];
        if (!string.IsNullOrWhiteSpace(configured))
            raw = configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        else
        {
            var path = Path.Combine(dataDirectory, FileName);
            if (File.Exists(path))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    root.GetProperty("schemaVersion").GetString() != SchemaVersion ||
                    root.GetProperty("origins").ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException($"{FileName} must use schema {SchemaVersion}.");
                raw = root.GetProperty("origins").EnumerateArray()
                    .Select(e => e.GetString() ?? throw new InvalidOperationException("Origins must be strings.")).ToArray();
            }
        }
        Origins = raw.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (Origins.Count > 32) throw new InvalidOperationException("At most 32 client origins may be configured.");
    }

    public IReadOnlyList<string> Origins { get; }

    public bool Allows(string origin) => Origins.Contains(origin, StringComparer.OrdinalIgnoreCase);

    private static string Normalize(string value)
    {
        // Exact origin form: scheme://host:port, no trailing slash or anything else.
        if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"^https?://(localhost|127\.0\.0\.1|\[::1\]):[0-9]{1,5}$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || !LoopbackHosts.Contains(uri.Host) ||
            uri.Port is < 1 or > 65535)
            throw new InvalidOperationException(
                $"Client origin '{value}' must be an exact loopback http(s) origin with a port, e.g. http://localhost:3000.");
        return value.ToLowerInvariant();
    }
}
