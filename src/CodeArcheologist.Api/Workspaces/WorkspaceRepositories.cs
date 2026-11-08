using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace CodeArcheologist.Api.Workspaces;

public sealed record RepositorySourceRecord(
    Guid Id,
    Guid WorkspaceId,
    string LocalPath,
    string SourceKind,
    DateTimeOffset AddedAtUtc,
    long Version);

public sealed record RepositoryPage(
    IReadOnlyList<RepositorySourceRecord> Items,
    long TotalCount,
    bool HasMore);

public sealed record RegisterRepositoryRequest(string? LocalPath);

public enum RepositoryRegistrationStatus
{
    Created,
    Replayed,
    DuplicatePath,
    ConflictingRequest
}

public sealed record RepositoryRegistrationResult(
    RepositoryRegistrationStatus Status,
    RepositorySourceRecord? Repository);

/// <summary>
/// Registration creates only metadata references. Snapshotting and all source
/// reads belong to a future, separately authorized ingestion service.
/// </summary>
public sealed partial class WorkspaceStore
{
    public RepositoryPage ListRepositories(Guid workspaceId, int limit = 100)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, workspace_id, local_path, source_kind, added_at_unix_ms, version
            FROM repository_sources
            WHERE workspace_id = $workspace
            ORDER BY added_at_unix_ms DESC, id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$workspace", workspaceId.ToString("D"));
        command.Parameters.AddWithValue("$limit", limit);

        var repositories = new List<RepositorySourceRecord>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                repositories.Add(ReadRepository(reader));
            }
        }

        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM repository_sources WHERE workspace_id = $workspace;";
        count.Parameters.AddWithValue("$workspace", workspaceId.ToString("D"));
        var total = Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        return new RepositoryPage(repositories, total, total > repositories.Count);
    }

    public RepositoryRegistrationResult RegisterRepository(
        Guid workspaceId, RepositoryLocation location, Guid idempotencyKey)
    {
        var key = idempotencyKey.ToString("D");
        var digest = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(
                "repository-registration-v1\n" + workspaceId.ToString("D") +
                "\n" + location.PathKey)));
        var id = Guid.NewGuid();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        using var connection = Open();
        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO repository_sources (
                id, workspace_id, local_path, path_key, source_kind,
                added_at_unix_ms, version, idempotency_key, request_digest
            )
            VALUES ($id, $workspace, $localPath, $pathKey, $kind,
                    $addedAt, 1, $key, $digest)
            ON CONFLICT DO NOTHING;
            """;
        insert.Parameters.AddWithValue("$id", id.ToString("D"));
        insert.Parameters.AddWithValue("$workspace", workspaceId.ToString("D"));
        insert.Parameters.AddWithValue("$localPath", location.LocalPath);
        insert.Parameters.AddWithValue("$pathKey", location.PathKey);
        insert.Parameters.AddWithValue("$kind", location.SourceKind);
        insert.Parameters.AddWithValue("$addedAt", timestamp);
        insert.Parameters.AddWithValue("$key", key);
        insert.Parameters.AddWithValue("$digest", digest);
        var created = insert.ExecuteNonQuery() == 1;

        using var lookup = connection.CreateCommand();
        lookup.CommandText = """
            SELECT id, workspace_id, local_path, source_kind, added_at_unix_ms,
                   version, request_digest
            FROM repository_sources
            WHERE idempotency_key = $key;
            """;
        lookup.Parameters.AddWithValue("$key", key);
        using var reader = lookup.ExecuteReader();
        if (!reader.Read())
        {
            // A different idempotency key already registered this directory
            // in the same workspace, and its unique path constraint won.
            return new RepositoryRegistrationResult(
                RepositoryRegistrationStatus.DuplicatePath, null);
        }

        var record = ReadRepository(reader);
        if (!string.Equals(reader.GetString(6), digest, StringComparison.Ordinal))
        {
            return new RepositoryRegistrationResult(
                RepositoryRegistrationStatus.ConflictingRequest, null);
        }

        return new RepositoryRegistrationResult(
            created ? RepositoryRegistrationStatus.Created : RepositoryRegistrationStatus.Replayed,
            record);
    }

    private static RepositorySourceRecord ReadRepository(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)),
        reader.GetString(2),
        reader.GetString(3),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
        reader.GetInt64(5));
}
