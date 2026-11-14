using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace CodeArcheologist.Api.Workspaces;

public sealed record WorkspaceRecord(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    long Version);

public sealed record WorkspacePage(
    IReadOnlyList<WorkspaceRecord> Items,
    long TotalCount,
    bool HasMore);

public sealed record CreateWorkspaceRequest(string? Name);

public sealed record CreateWorkspaceResult(
    WorkspaceRecord Workspace,
    bool Created,
    bool Conflict);

/// <summary>
/// The first, metadata-only SQLite slice. No repository bytes, credentials,
/// analysis results or Obsidian notes are stored by this service.
/// </summary>
public sealed partial class WorkspaceStore
{
    private const int CurrentSchemaVersion = 9;
    private readonly string _connectionString;

    public WorkspaceStore()
    {
        // The optional override exists for disposable, isolated integration tests.
        // Normal users do not need to set any environment variables.
        var dataDirectory = Environment.GetEnvironmentVariable("CODE_ARCHEOLOGIST_DATA_DIR");
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            var userData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(userData))
            {
                throw new InvalidOperationException("No local application data directory is available.");
            }

            dataDirectory = Path.Combine(userData, "CodeArcheologist");
        }

        var root = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(root);
        DataDirectory = root;
        DatabasePath = Path.Combine(root, "workspaces.sqlite3");

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
    }

    public string DatabasePath { get; }

    /// <summary>Local application data directory that also holds the OS execution lock.</summary>
    public string DataDirectory { get; }

    /// <summary>
    /// Opens and migrates SQLite ONLY. Since C6b this never reconciles,
    /// interrupts or recovers investigation work: that requires the OS
    /// execution lock and a fenced ownership epoch (see ReconcileAbandoned).
    /// A second process may therefore safely call this while another
    /// process is executing an investigation.
    /// </summary>
    public void Initialize() => InitializeTo(CurrentSchemaVersion);

    /// <summary>Test seam: migrate to an older schema to verify forward migration.</summary>
    internal void InitializeTo(int targetVersion)
    {
        if (targetVersion is < 5 or > CurrentSchemaVersion)
            throw new ArgumentOutOfRangeException(nameof(targetVersion));
        using var connection = Open();
        using (var journal = connection.CreateCommand())
        {
            journal.CommandText = "PRAGMA journal_mode = WAL;";
            journal.ExecuteNonQuery();
        }
        // The v6 table rebuilds follow SQLite's documented procedure, which
        // requires foreign-key enforcement to be suspended OUTSIDE a transaction
        // and an explicit foreign_key_check before commit. The connection is
        // disposed afterwards; every other connection enables foreign keys.
        using (var fk = connection.CreateCommand())
        {
            fk.CommandText = "PRAGMA foreign_keys = OFF;";
            fk.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();
        using var versionQuery = connection.CreateCommand();
        versionQuery.Transaction = transaction;
        versionQuery.CommandText = "PRAGMA user_version;";
        var current = Convert.ToInt32(versionQuery.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (current >= targetVersion && current <= CurrentSchemaVersion)
        {
            transaction.Commit();
            return;
        }
        if (current > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Workspace database schema {current} is newer than supported version {CurrentSchemaVersion}.");
        }

        if (current == 0)
        {
            using var migration = connection.CreateCommand();
            migration.Transaction = transaction;
            migration.CommandText = """
                CREATE TABLE IF NOT EXISTS workspaces (
                    id TEXT NOT NULL PRIMARY KEY,
                    name TEXT NOT NULL,
                    created_at_unix_ms INTEGER NOT NULL,
                    updated_at_unix_ms INTEGER NOT NULL,
                    version INTEGER NOT NULL CHECK(version >= 1),
                    idempotency_key TEXT NOT NULL UNIQUE,
                    request_digest TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_workspaces_recent
                    ON workspaces(created_at_unix_ms DESC, id DESC);
                PRAGMA user_version = 1;
                """;
            migration.ExecuteNonQuery();
        }

        if (current < 2)
        {
            using var migration = connection.CreateCommand();
            migration.Transaction = transaction;
            migration.CommandText = """
                CREATE TABLE repository_sources (
                    id TEXT NOT NULL PRIMARY KEY,
                    workspace_id TEXT NOT NULL REFERENCES workspaces(id) ON DELETE CASCADE,
                    local_path TEXT NOT NULL,
                    path_key TEXT NOT NULL,
                    source_kind TEXT NOT NULL CHECK(source_kind IN ('git', 'folder')),
                    added_at_unix_ms INTEGER NOT NULL,
                    version INTEGER NOT NULL CHECK(version >= 1),
                    idempotency_key TEXT NOT NULL UNIQUE,
                    request_digest TEXT NOT NULL,
                    UNIQUE(workspace_id, path_key)
                );
                CREATE INDEX ix_repository_sources_workspace
                    ON repository_sources(workspace_id, added_at_unix_ms DESC, id DESC);
                PRAGMA user_version = 2;
                """;
            migration.ExecuteNonQuery();
        }

        if (current < 3)
        {
            using var migration = connection.CreateCommand();
            migration.Transaction = transaction;
            migration.CommandText = """
                CREATE TABLE repository_snapshots (
                    id TEXT NOT NULL PRIMARY KEY,
                    workspace_id TEXT NOT NULL REFERENCES workspaces(id),
                    repository_id TEXT NOT NULL REFERENCES repository_sources(id),
                    fingerprint_sha256 TEXT NOT NULL,
                    manifest_sha256 TEXT NOT NULL,
                    captured_at_unix_ms INTEGER NOT NULL,
                    file_count INTEGER NOT NULL CHECK(file_count >= 0),
                    total_bytes INTEGER NOT NULL CHECK(total_bytes >= 0),
                    exclusion_count INTEGER NOT NULL CHECK(exclusion_count >= 0),
                    policy_version TEXT NOT NULL,
                    UNIQUE(repository_id, fingerprint_sha256)
                );
                CREATE INDEX ix_snapshots_by_repository
                    ON repository_snapshots(repository_id, captured_at_unix_ms DESC, id DESC);
                CREATE TABLE snapshot_requests (
                    idempotency_key TEXT NOT NULL PRIMARY KEY,
                    request_digest TEXT NOT NULL,
                    snapshot_id TEXT NOT NULL REFERENCES repository_snapshots(id)
                );
                PRAGMA user_version = 3;
                """;
            migration.ExecuteNonQuery();
        }

        if (current < 4)
        {
            using var migration = connection.CreateCommand();
            migration.Transaction = transaction;
            migration.CommandText = """
                CREATE TABLE investigation_reports (
                    run_id TEXT NOT NULL PRIMARY KEY,
                    workspace_id TEXT NOT NULL REFERENCES workspaces(id),
                    repository_id TEXT NOT NULL REFERENCES repository_sources(id),
                    snapshot_id TEXT NOT NULL REFERENCES repository_snapshots(id),
                    fingerprint_sha256 TEXT NOT NULL,
                    entry_point_id TEXT NOT NULL,
                    evidence_contract_version TEXT NOT NULL,
                    report_contract_version TEXT NOT NULL,
                    started_at_unix_ms INTEGER NOT NULL,
                    finished_at_unix_ms INTEGER,
                    status TEXT NOT NULL CHECK(status IN ('running','completed','incomplete','cancelled','rejected')),
                    diagnostic TEXT NOT NULL,
                    report_json TEXT,
                    idempotency_key TEXT NOT NULL UNIQUE,
                    request_digest TEXT NOT NULL
                );
                CREATE INDEX ix_investigations_by_snapshot
                    ON investigation_reports(snapshot_id, started_at_unix_ms DESC, run_id DESC);
                PRAGMA user_version = 4;
                """;
            migration.ExecuteNonQuery();
        }


        if (current < 5)
        {
            using var migration = connection.CreateCommand();
            migration.Transaction = transaction;
            migration.CommandText = """
                CREATE TABLE investigation_attempts (
                    attempt_id TEXT NOT NULL PRIMARY KEY,
                    run_id TEXT NOT NULL REFERENCES investigation_reports(run_id),
                    attempt_number INTEGER NOT NULL CHECK(attempt_number >= 1),
                    contract_version TEXT NOT NULL,
                    status TEXT NOT NULL CHECK(status IN
                        ('queued','running','completed','incomplete','interrupted',
                         'cancelled','rejected','failed')),
                    diagnostic TEXT NOT NULL,
                    created_at_unix_ms INTEGER NOT NULL,
                    started_at_unix_ms INTEGER,
                    finished_at_unix_ms INTEGER,
                    last_event_sequence INTEGER NOT NULL DEFAULT 0
                        CHECK(last_event_sequence >= 0),
                    state_version INTEGER NOT NULL DEFAULT 1
                        CHECK(state_version >= 1),
                    execution_owner_id TEXT,
                    ownership_epoch INTEGER,
                    UNIQUE(run_id, attempt_number)
                );
                CREATE UNIQUE INDEX ux_attempts_active_per_run
                    ON investigation_attempts(run_id)
                    WHERE status IN ('queued','running');
                CREATE INDEX ix_attempts_by_run
                    ON investigation_attempts(run_id, attempt_number DESC);

                CREATE TABLE investigation_events (
                    event_id TEXT NOT NULL PRIMARY KEY,
                    attempt_id TEXT NOT NULL REFERENCES investigation_attempts(attempt_id),
                    sequence_number INTEGER NOT NULL CHECK(sequence_number >= 1),
                    event_contract_version TEXT NOT NULL,
                    occurred_at_unix_ms INTEGER NOT NULL,
                    event_type TEXT NOT NULL CHECK(event_type IN (
                        'attempt-admitted','attempt-started','attempt-completed',
                        'attempt-incomplete','attempt-interrupted','attempt-cancelled',
                        'attempt-rejected','attempt-failed',
                        'evidence-verification-started','evidence-verification-accepted',
                        'evidence-verification-rejected','role-started',
                        'role-assessment-accepted','role-assessment-rejected',
                        'checkpoint-accepted')),
                    diagnostic TEXT NOT NULL,
                    stage TEXT,
                    from_status TEXT,
                    to_status TEXT,
                    checkpoint_id TEXT,
                    payload_json TEXT NOT NULL CHECK(length(payload_json) <= 16384),
                    UNIQUE(attempt_id, sequence_number)
                );
                CREATE INDEX ix_events_by_attempt
                    ON investigation_events(attempt_id, sequence_number);
                CREATE TRIGGER investigation_events_no_update
                    BEFORE UPDATE ON investigation_events BEGIN
                    SELECT RAISE(ABORT, 'events are immutable');
                END;
                CREATE TRIGGER investigation_events_no_delete
                    BEFORE DELETE ON investigation_events BEGIN
                    SELECT RAISE(ABORT, 'events are immutable');
                END;

                CREATE TABLE investigation_checkpoints (
                    checkpoint_id TEXT NOT NULL PRIMARY KEY,
                    attempt_id TEXT NOT NULL REFERENCES investigation_attempts(attempt_id),
                    role TEXT NOT NULL CHECK(role IN ('investigator','skeptic','reviewer')),
                    contract_version TEXT NOT NULL,
                    evidence_contract_version TEXT NOT NULL,
                    analyzer_provenance_json TEXT NOT NULL,
                    model_provenance_json TEXT NOT NULL,
                    scope_json TEXT NOT NULL,
                    assessment_json TEXT NOT NULL,
                    evidence_links_json TEXT NOT NULL,
                    validation_json TEXT NOT NULL,
                    content_sha256 TEXT NOT NULL,
                    accepted_at_unix_ms INTEGER NOT NULL,
                    UNIQUE(attempt_id, role)
                );
                CREATE TRIGGER investigation_checkpoints_no_update
                    BEFORE UPDATE ON investigation_checkpoints BEGIN
                    SELECT RAISE(ABORT, 'checkpoints are immutable');
                END;
                CREATE TRIGGER investigation_checkpoints_no_delete
                    BEFORE DELETE ON investigation_checkpoints BEGIN
                    SELECT RAISE(ABORT, 'checkpoints are immutable');
                END;

                CREATE TABLE investigation_execution_slot (
                    slot_id INTEGER NOT NULL PRIMARY KEY CHECK(slot_id = 1),
                    active_attempt_id TEXT REFERENCES investigation_attempts(attempt_id)
                );
                INSERT INTO investigation_execution_slot(slot_id,active_attempt_id)
                    VALUES (1,NULL);
                PRAGMA user_version = 5;
                """;
            migration.ExecuteNonQuery();
        }

        if (current < 6 && targetVersion >= 6)
            MigrateToV6(connection, transaction);
        if (current < 7 && targetVersion >= 7)
            MigrateToV7(connection, transaction);
        if (current < 8 && targetVersion >= 8)
            MigrateToV8(connection, transaction);
        if (current < 9 && targetVersion >= 9)
            MigrateToV9(connection, transaction);

        using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "PRAGMA foreign_key_check;";
            using var violations = check.ExecuteReader();
            if (violations.Read())
                throw new InvalidOperationException("Schema migration produced foreign-key violations.");
        }
        transaction.Commit();
    }

    public WorkspacePage List(int limit)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, created_at_unix_ms, updated_at_unix_ms, version
            FROM workspaces
            ORDER BY created_at_unix_ms DESC, id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var workspaces = new List<WorkspaceRecord>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                workspaces.Add(Read(reader));
            }
        }

        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM workspaces;";
        var total = Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        return new WorkspacePage(workspaces, total, total > workspaces.Count);
    }

    public WorkspaceRecord? Get(Guid id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, created_at_unix_ms, updated_at_unix_ms, version
            FROM workspaces WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public CreateWorkspaceResult Create(string name, Guid idempotencyKey)
    {
        var key = idempotencyKey.ToString("D");
        var digest = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes("workspace-create-v1\n" + name)));
        var id = Guid.NewGuid();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        using var connection = Open();
        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO workspaces (
                id, name, created_at_unix_ms, updated_at_unix_ms,
                version, idempotency_key, request_digest
            )
            VALUES ($id, $name, $created, $updated, 1, $key, $digest)
            ON CONFLICT(idempotency_key) DO NOTHING;
            """;
        insert.Parameters.AddWithValue("$id", id.ToString("D"));
        insert.Parameters.AddWithValue("$name", name);
        insert.Parameters.AddWithValue("$created", timestamp);
        insert.Parameters.AddWithValue("$updated", timestamp);
        insert.Parameters.AddWithValue("$key", key);
        insert.Parameters.AddWithValue("$digest", digest);
        var created = insert.ExecuteNonQuery() == 1;

        using var lookup = connection.CreateCommand();
        lookup.CommandText = """
            SELECT id, name, created_at_unix_ms, updated_at_unix_ms, version,
                   request_digest
            FROM workspaces
            WHERE idempotency_key = $key;
            """;
        lookup.Parameters.AddWithValue("$key", key);

        using var reader = lookup.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException("The workspace creation outcome was not persisted.");
        }

        var record = Read(reader);
        var sameRequest = string.Equals(reader.GetString(5), digest, StringComparison.Ordinal);
        return new CreateWorkspaceResult(record, created, !sameRequest);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static WorkspaceRecord Read(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
        reader.GetInt64(4));
}
