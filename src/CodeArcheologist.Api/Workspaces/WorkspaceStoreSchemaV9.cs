using Microsoft.Data.Sqlite;

namespace CodeArcheologist.Api.Workspaces;

/// <summary>
/// Additive, opt-in V2 observation only. Never changes v2/v3 checkpoint,
/// R3 adaptive receipt, or historical investigation event contracts.
/// </summary>
public sealed partial class WorkspaceStore
{
    private static void MigrateToV9(SqliteConnection db, SqliteTransaction tx)
    {
        using var migration = Command(db, tx, """
            CREATE TABLE v2_handoff_optins (
                run_id TEXT NOT NULL PRIMARY KEY REFERENCES investigation_reports(run_id),
                contract_version TEXT NOT NULL
                    CHECK(contract_version='v2-model-handoff-observation-v1'),
                created_at_unix_ms INTEGER NOT NULL
            );
            CREATE TRIGGER v2_handoff_optins_no_update
                BEFORE UPDATE ON v2_handoff_optins BEGIN
                SELECT RAISE(ABORT, 'v2 opt-ins are immutable');
            END;
            CREATE TRIGGER v2_handoff_optins_no_delete
                BEFORE DELETE ON v2_handoff_optins BEGIN
                SELECT RAISE(ABORT, 'v2 opt-ins are immutable');
            END;
            CREATE TABLE v2_handoff_events (
                event_id TEXT NOT NULL PRIMARY KEY,
                call_id TEXT NOT NULL,
                run_id TEXT NOT NULL REFERENCES v2_handoff_optins(run_id),
                attempt_id TEXT NOT NULL REFERENCES investigation_attempts(attempt_id),
                role TEXT NOT NULL CHECK(role IN ('investigator','skeptic','reviewer')),
                call_ordinal INTEGER NOT NULL CHECK(call_ordinal BETWEEN 1 AND 12),
                stage TEXT NOT NULL CHECK(length(stage) BETWEEN 1 AND 80),
                event_kind TEXT NOT NULL
                    CHECK(event_kind IN ('prepared','adapter-entered','completed','faulted')),
                prompt_sha256 TEXT NOT NULL CHECK(length(prompt_sha256)=64),
                evidence_links_json TEXT NOT NULL CHECK(length(evidence_links_json) BETWEEN 2 AND 65536),
                receipt_ids_json TEXT NOT NULL CHECK(length(receipt_ids_json) BETWEEN 2 AND 4096),
                snapshot_fingerprint TEXT NOT NULL CHECK(length(snapshot_fingerprint)=64),
                ownership_epoch INTEGER NOT NULL CHECK(ownership_epoch >= 1),
                created_at_unix_ms INTEGER NOT NULL,
                content_sha256 TEXT NOT NULL CHECK(length(content_sha256)=64),
                UNIQUE(attempt_id,role,call_ordinal,event_kind)
            );
            CREATE INDEX ix_v2_handoff_events_run
                ON v2_handoff_events(run_id,attempt_id,role,call_ordinal,event_kind);
            CREATE TRIGGER v2_handoff_events_no_update
                BEFORE UPDATE ON v2_handoff_events BEGIN
                SELECT RAISE(ABORT, 'v2 handoff events are immutable');
            END;
            CREATE TRIGGER v2_handoff_events_no_delete
                BEFORE DELETE ON v2_handoff_events BEGIN
                SELECT RAISE(ABORT, 'v2 handoff events are immutable');
            END;
            PRAGMA user_version = 9;
            """);
        migration.ExecuteNonQuery();
    }
}
