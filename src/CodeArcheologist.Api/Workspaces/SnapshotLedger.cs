using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CodeArcheologist.Api.Workspaces;

public sealed record SnapshotRecord(
    Guid Id,
    Guid WorkspaceId,
    Guid RepositoryId,
    string FingerprintSha256,
    string ManifestSha256,
    DateTimeOffset CapturedAtUtc,
    int FileCount,
    long TotalBytes,
    int ExclusionCount,
    string PolicyVersion);

public sealed record SnapshotPage(
    IReadOnlyList<SnapshotRecord> Items,
    long TotalCount,
    bool HasMore);

public sealed record SnapshotRequestResolution(SnapshotRecord? Existing, bool Conflict);

/// <summary>
/// Durable identities and request idempotency for completed captures.
/// Filesystem snapshot data is stored separately under app-managed paths.
/// </summary>
public sealed partial class WorkspaceStore
{
    public SnapshotRequestResolution ResolveSnapshotRequest(Guid requestKey, string requestDigest)
    {
        using var connection = Open();
        using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT s.id, s.workspace_id, s.repository_id, s.fingerprint_sha256,
                   s.manifest_sha256, s.captured_at_unix_ms, s.file_count,
                   s.total_bytes, s.exclusion_count, s.policy_version,
                   q.request_digest
            FROM snapshot_requests q JOIN repository_snapshots s ON s.id = q.snapshot_id
            WHERE q.idempotency_key = $key;
            """;
        query.Parameters.AddWithValue("$key", requestKey.ToString("D"));
        using var reader = query.ExecuteReader();
        if (!reader.Read()) return new SnapshotRequestResolution(null, false);
        if (!string.Equals(reader.GetString(10), requestDigest, StringComparison.Ordinal))
        {
            return new SnapshotRequestResolution(null, true);
        }
        return new SnapshotRequestResolution(ReadSnapshot(reader), false);
    }

    public SnapshotRecord? FindSnapshotByFingerprint(Guid repositoryId, string fingerprint)
    {
        using var connection = Open();
        using var query = connection.CreateCommand();
        query.CommandText = SnapshotColumns + """
            WHERE repository_id = $repo AND fingerprint_sha256 = $fingerprint;
            """;
        query.Parameters.AddWithValue("$repo", repositoryId.ToString("D"));
        query.Parameters.AddWithValue("$fingerprint", fingerprint);
        using var reader = query.ExecuteReader();
        return reader.Read() ? ReadSnapshot(reader) : null;
    }

    public SnapshotRecord? GetSnapshot(Guid workspaceId, Guid repositoryId, Guid snapshotId)
    {
        using var connection = Open();
        using var query = connection.CreateCommand();
        query.CommandText = SnapshotColumns + """
            WHERE workspace_id = $workspace AND repository_id = $repo AND id = $id;
            """;
        query.Parameters.AddWithValue("$workspace", workspaceId.ToString("D"));
        query.Parameters.AddWithValue("$repo", repositoryId.ToString("D"));
        query.Parameters.AddWithValue("$id", snapshotId.ToString("D"));
        using var reader = query.ExecuteReader();
        return reader.Read() ? ReadSnapshot(reader) : null;
    }

    public SnapshotPage ListSnapshots(Guid workspaceId, Guid repositoryId, int limit = 100)
    {
        using var connection = Open();
        using var query = connection.CreateCommand();
        query.CommandText = SnapshotColumns + """
            WHERE workspace_id = $workspace AND repository_id = $repo
            ORDER BY captured_at_unix_ms DESC, id DESC LIMIT $limit;
            """;
        query.Parameters.AddWithValue("$workspace", workspaceId.ToString("D"));
        query.Parameters.AddWithValue("$repo", repositoryId.ToString("D"));
        query.Parameters.AddWithValue("$limit", limit);

        var entries = new List<SnapshotRecord>();
        using (var reader = query.ExecuteReader())
        {
            while (reader.Read()) entries.Add(ReadSnapshot(reader));
        }

        using var count = connection.CreateCommand();
        count.CommandText = """
            SELECT COUNT(*) FROM repository_snapshots
            WHERE workspace_id = $workspace AND repository_id = $repo;
            """;
        count.Parameters.AddWithValue("$workspace", workspaceId.ToString("D"));
        count.Parameters.AddWithValue("$repo", repositoryId.ToString("D"));
        var total = Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        return new SnapshotPage(entries, total, total > entries.Count);
    }

    public void SaveSnapshot(SnapshotRecord snapshot, Guid requestKey,
        string requestDigest, bool isNew)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        if (isNew)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO repository_snapshots (
                    id, workspace_id, repository_id, fingerprint_sha256,
                    manifest_sha256, captured_at_unix_ms, file_count, total_bytes,
                    exclusion_count, policy_version
                )
                VALUES ($id, $workspace, $repo, $fingerprint, $manifest,
                        $captured, $count, $bytes, $exclusions, $policy);
                """;
            insert.Parameters.AddWithValue("$id", snapshot.Id.ToString("D"));
            insert.Parameters.AddWithValue("$workspace", snapshot.WorkspaceId.ToString("D"));
            insert.Parameters.AddWithValue("$repo", snapshot.RepositoryId.ToString("D"));
            insert.Parameters.AddWithValue("$fingerprint", snapshot.FingerprintSha256);
            insert.Parameters.AddWithValue("$manifest", snapshot.ManifestSha256);
            insert.Parameters.AddWithValue("$captured", snapshot.CapturedAtUtc.ToUnixTimeMilliseconds());
            insert.Parameters.AddWithValue("$count", snapshot.FileCount);
            insert.Parameters.AddWithValue("$bytes", snapshot.TotalBytes);
            insert.Parameters.AddWithValue("$exclusions", snapshot.ExclusionCount);
            insert.Parameters.AddWithValue("$policy", snapshot.PolicyVersion);
            insert.ExecuteNonQuery();
        }

        using var request = connection.CreateCommand();
        request.Transaction = transaction;
        request.CommandText = """
            INSERT INTO snapshot_requests(idempotency_key, request_digest, snapshot_id)
            VALUES ($key, $digest, $snapshot);
            """;
        request.Parameters.AddWithValue("$key", requestKey.ToString("D"));
        request.Parameters.AddWithValue("$digest", requestDigest);
        request.Parameters.AddWithValue("$snapshot", snapshot.Id.ToString("D"));
        request.ExecuteNonQuery();
        transaction.Commit();
    }

    private const string SnapshotColumns = """
        SELECT id, workspace_id, repository_id, fingerprint_sha256,
               manifest_sha256, captured_at_unix_ms, file_count, total_bytes,
               exclusion_count, policy_version
        FROM repository_snapshots
        """;

    private static SnapshotRecord ReadSnapshot(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)),
        Guid.Parse(reader.GetString(2)),
        reader.GetString(3),
        reader.GetString(4),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
        reader.GetInt32(6),
        reader.GetInt64(7),
        reader.GetInt32(8),
        reader.GetString(9));
}
