using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CodeArcheologist.Api.Workspaces;

/// <summary>
/// Gate C6b forward-only schema v6. Runs inside the single migration
/// transaction of <see cref="WorkspaceStore.Initialize"/> with foreign-key
/// enforcement suspended for the documented SQLite table-rebuild procedure;
/// <c>PRAGMA foreign_key_check</c> must be clean before commit.
/// Historical reports, attempts and events are copied unchanged; nothing is
/// converted into resumable state.
/// </summary>
public sealed partial class WorkspaceStore
{
    // Terminal causes are host-classified categories, never model or exception text.
    internal const string TerminalCauses = """
        'completed','user-cancelled','attempt-deadline','role-timeout',
        'application-shutdown','process-interrupted','provider-error',
        'validation-rejected','evidence-rejected','ownership-lost',
        'report-validation-failed','host-failure','legacy-c6a-restart'
        """;

    internal const string EventTypes = """
        'attempt-admitted','attempt-started','attempt-completed',
        'attempt-incomplete','attempt-interrupted','attempt-cancelled',
        'attempt-rejected','attempt-failed',
        'evidence-verification-started','evidence-verification-accepted',
        'evidence-verification-rejected','role-started',
        'role-assessment-accepted','role-assessment-rejected',
        'role-failed','role-timed-out','role-cancelled',
        'checkpoint-accepted','checkpoint-reused','checkpoint-validation-rejected',
        'cancellation-requested','continuation-admitted','ownership-lost'
        """;

    private static void MigrateToV6(SqliteConnection connection, SqliteTransaction transaction)
    {
        // v5 reserved the checkpoint table but C6a never wrote it. A row here
        // has unknown provenance; refuse to guess how to carry it forward.
        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT COUNT(*) FROM investigation_checkpoints;";
            if (Convert.ToInt64(existing.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                throw new InvalidOperationException(
                    "Schema v5 contains checkpoint rows of unknown provenance; refusing v6 migration.");
        }

        using var migration = connection.CreateCommand();
        migration.Transaction = transaction;
        migration.CommandText = $"""
            -- 1. Run-level 'interrupted' report state, separate from attempt state.
            CREATE TABLE investigation_reports_v6 (
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
                status TEXT NOT NULL CHECK(status IN
                    ('running','interrupted','completed','incomplete','cancelled','rejected')),
                diagnostic TEXT NOT NULL,
                report_json TEXT,
                idempotency_key TEXT NOT NULL UNIQUE,
                request_digest TEXT NOT NULL,
                CHECK(status <> 'interrupted' OR report_json IS NULL)
            );
            INSERT INTO investigation_reports_v6 (
                run_id, workspace_id, repository_id, snapshot_id, fingerprint_sha256,
                entry_point_id, evidence_contract_version, report_contract_version,
                started_at_unix_ms, finished_at_unix_ms, status, diagnostic,
                report_json, idempotency_key, request_digest)
            SELECT run_id, workspace_id, repository_id, snapshot_id, fingerprint_sha256,
                entry_point_id, evidence_contract_version, report_contract_version,
                started_at_unix_ms, finished_at_unix_ms, status, diagnostic,
                report_json, idempotency_key, request_digest
            FROM investigation_reports;
            DROP TABLE investigation_reports;
            ALTER TABLE investigation_reports_v6 RENAME TO investigation_reports;
            CREATE INDEX ix_investigations_by_snapshot
                ON investigation_reports(snapshot_id, started_at_unix_ms DESC, run_id DESC);

            -- 2. Attempt lineage, terminal causes, deadlines and cancellation intent.
            ALTER TABLE investigation_attempts ADD COLUMN continues_attempt_id TEXT
                REFERENCES investigation_attempts(attempt_id);
            ALTER TABLE investigation_attempts ADD COLUMN terminal_cause TEXT
                CHECK(terminal_cause IS NULL OR terminal_cause IN ({TerminalCauses}));
            ALTER TABLE investigation_attempts ADD COLUMN deadline_at_unix_ms INTEGER;
            ALTER TABLE investigation_attempts ADD COLUMN cancel_requested_at_unix_ms INTEGER;

            -- 3. Expanded append-only event taxonomy with ownership epoch.
            CREATE TABLE investigation_events_v6 (
                event_id TEXT NOT NULL PRIMARY KEY,
                attempt_id TEXT NOT NULL REFERENCES investigation_attempts(attempt_id),
                sequence_number INTEGER NOT NULL CHECK(sequence_number >= 1),
                event_contract_version TEXT NOT NULL,
                occurred_at_unix_ms INTEGER NOT NULL,
                event_type TEXT NOT NULL CHECK(event_type IN ({EventTypes})),
                diagnostic TEXT NOT NULL,
                stage TEXT CHECK(stage IS NULL OR stage IN ('investigator','skeptic','reviewer','report')),
                from_status TEXT,
                to_status TEXT,
                checkpoint_id TEXT,
                payload_json TEXT NOT NULL CHECK(length(payload_json) <= 16384),
                ownership_epoch INTEGER,
                UNIQUE(attempt_id, sequence_number)
            );
            INSERT INTO investigation_events_v6 (
                event_id, attempt_id, sequence_number, event_contract_version,
                occurred_at_unix_ms, event_type, diagnostic, stage, from_status,
                to_status, checkpoint_id, payload_json, ownership_epoch)
            SELECT event_id, attempt_id, sequence_number, event_contract_version,
                occurred_at_unix_ms, event_type, diagnostic, stage, from_status,
                to_status, checkpoint_id, payload_json, NULL
            FROM investigation_events;
            DROP TABLE investigation_events;
            ALTER TABLE investigation_events_v6 RENAME TO investigation_events;
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

            -- 4. Run-scoped immutable role checkpoints (empty in v5, verified above).
            DROP TABLE investigation_checkpoints;
            CREATE TABLE investigation_checkpoints (
                checkpoint_id TEXT NOT NULL PRIMARY KEY,
                run_id TEXT NOT NULL REFERENCES investigation_reports(run_id),
                attempt_id TEXT NOT NULL REFERENCES investigation_attempts(attempt_id),
                role TEXT NOT NULL CHECK(role IN ('investigator','skeptic','reviewer')),
                checkpoint_contract_version TEXT NOT NULL,
                workspace_id TEXT NOT NULL,
                repository_id TEXT NOT NULL,
                snapshot_id TEXT NOT NULL,
                fingerprint_sha256 TEXT NOT NULL,
                entry_point_id TEXT NOT NULL,
                evidence_contract_version TEXT NOT NULL,
                capability_contract_version TEXT NOT NULL,
                analyzer_version TEXT NOT NULL,
                model_id TEXT NOT NULL,
                validator_version TEXT NOT NULL,
                assessment_contract_version TEXT NOT NULL,
                assessment_json TEXT NOT NULL,
                evidence_links_json TEXT NOT NULL,
                validation_json TEXT NOT NULL,
                depends_on_json TEXT NOT NULL,
                content_sha256 TEXT NOT NULL CHECK(length(content_sha256) = 64),
                accepted_at_unix_ms INTEGER NOT NULL,
                accepted_ownership_epoch INTEGER NOT NULL CHECK(accepted_ownership_epoch >= 1),
                UNIQUE(run_id, role)
            );
            CREATE TRIGGER investigation_checkpoints_no_update
                BEFORE UPDATE ON investigation_checkpoints BEGIN
                SELECT RAISE(ABORT, 'checkpoints are immutable');
            END;
            CREATE TRIGGER investigation_checkpoints_no_delete
                BEFORE DELETE ON investigation_checkpoints BEGIN
                SELECT RAISE(ABORT, 'checkpoints are immutable');
            END;

            -- 5. Which checkpoints each attempt produced or reused (append-only).
            CREATE TABLE attempt_checkpoint_lineage (
                attempt_id TEXT NOT NULL REFERENCES investigation_attempts(attempt_id),
                checkpoint_id TEXT NOT NULL REFERENCES investigation_checkpoints(checkpoint_id),
                relation TEXT NOT NULL CHECK(relation IN ('accepted','reused')),
                recorded_at_unix_ms INTEGER NOT NULL,
                PRIMARY KEY(attempt_id, checkpoint_id)
            );
            CREATE TRIGGER attempt_checkpoint_lineage_no_update
                BEFORE UPDATE ON attempt_checkpoint_lineage BEGIN
                SELECT RAISE(ABORT, 'lineage is immutable');
            END;
            CREATE TRIGGER attempt_checkpoint_lineage_no_delete
                BEFORE DELETE ON attempt_checkpoint_lineage BEGIN
                SELECT RAISE(ABORT, 'lineage is immutable');
            END;

            -- 6. Durable idempotency for continuation and cancellation mutations.
            CREATE TABLE investigation_execution_requests (
                idempotency_key TEXT NOT NULL PRIMARY KEY,
                request_kind TEXT NOT NULL CHECK(request_kind IN ('continue','cancel')),
                request_digest TEXT NOT NULL,
                run_id TEXT NOT NULL REFERENCES investigation_reports(run_id),
                attempt_id TEXT REFERENCES investigation_attempts(attempt_id),
                created_at_unix_ms INTEGER NOT NULL
            );

            -- 7. Fenced execution owner. Authority additionally requires the OS
            -- lock; heartbeat age NEVER transfers ownership.
            CREATE TABLE execution_owner (
                owner_slot INTEGER NOT NULL PRIMARY KEY CHECK(owner_slot = 1),
                owner_id TEXT,
                ownership_epoch INTEGER NOT NULL CHECK(ownership_epoch >= 0),
                status TEXT NOT NULL CHECK(status IN ('unowned','active','released')),
                acquired_at_unix_ms INTEGER,
                heartbeat_at_unix_ms INTEGER,
                process_id INTEGER,
                diagnostic TEXT NOT NULL
            );
            INSERT INTO execution_owner (owner_slot, owner_id, ownership_epoch, status, diagnostic)
                VALUES (1, NULL, 0, 'unowned', 'never-owned');
            CREATE TRIGGER execution_owner_epoch_monotonic
                BEFORE UPDATE ON execution_owner
                WHEN NEW.ownership_epoch <> OLD.ownership_epoch
                     AND NEW.ownership_epoch <> OLD.ownership_epoch + 1
                BEGIN SELECT RAISE(ABORT, 'ownership epoch must advance by one'); END;
            CREATE TRIGGER execution_owner_identity_needs_epoch
                BEFORE UPDATE ON execution_owner
                WHEN NEW.owner_id IS NOT OLD.owner_id
                     AND NEW.ownership_epoch = OLD.ownership_epoch
                BEGIN SELECT RAISE(ABORT, 'owner change requires a new epoch'); END;
            CREATE TRIGGER execution_owner_no_delete
                BEFORE DELETE ON execution_owner BEGIN
                SELECT RAISE(ABORT, 'execution owner row is permanent');
            END;
            CREATE TABLE execution_ownership_history (
                ownership_epoch INTEGER NOT NULL PRIMARY KEY CHECK(ownership_epoch >= 1),
                owner_id TEXT NOT NULL UNIQUE,
                acquired_at_unix_ms INTEGER NOT NULL,
                process_id INTEGER NOT NULL
            );
            CREATE TRIGGER execution_ownership_history_no_update
                BEFORE UPDATE ON execution_ownership_history BEGIN
                SELECT RAISE(ABORT, 'ownership history is immutable');
            END;
            CREATE TRIGGER execution_ownership_history_no_delete
                BEFORE DELETE ON execution_ownership_history BEGIN
                SELECT RAISE(ABORT, 'ownership history is immutable');
            END;

            PRAGMA user_version = 6;
            """;
        migration.ExecuteNonQuery();
    }
}
