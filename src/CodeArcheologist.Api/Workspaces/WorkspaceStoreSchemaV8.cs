using Microsoft.Data.Sqlite;

namespace CodeArcheologist.Api.Workspaces;

/// <summary>
/// R3 adaptive-evidence storage foundation. Additive only: historical C6b
/// events, checkpoints, attempt records and question contexts are untouched.
/// The runtime remains legacy Gate C until a separately verified versioned
/// adaptive dispatcher is enabled.
/// </summary>
public sealed partial class WorkspaceStore
{
    private static void MigrateToV8(SqliteConnection db, SqliteTransaction tx)
    {
        using var migration = Command(db, tx, """
            CREATE TABLE adaptive_question_contexts (
                run_id TEXT NOT NULL PRIMARY KEY REFERENCES investigation_reports(run_id),
                question_id TEXT NOT NULL UNIQUE,
                contract_version TEXT NOT NULL
                    CHECK(contract_version = 'r3-adaptive-question-context-v2'),
                subject_node_id TEXT NOT NULL,
                subject_node_kind TEXT NOT NULL
                    CHECK(subject_node_kind IN ('http-endpoint-attribute','http-minimal-mapping')),
                question_kind TEXT NOT NULL CHECK(question_kind='explain-http-entry-point'),
                context_json TEXT NOT NULL CHECK(length(context_json) BETWEEN 1 AND 8192),
                context_digest TEXT NOT NULL CHECK(length(context_digest)=64),
                created_at_unix_ms INTEGER NOT NULL
            );
            CREATE TRIGGER adaptive_question_contexts_no_update
                BEFORE UPDATE ON adaptive_question_contexts BEGIN
                SELECT RAISE(ABORT,'adaptive question contexts are immutable');
            END;
            CREATE TRIGGER adaptive_question_contexts_no_delete
                BEFORE DELETE ON adaptive_question_contexts BEGIN
                SELECT RAISE(ABORT,'adaptive question contexts are immutable');
            END;
            CREATE TABLE adaptive_evidence_receipts (
                receipt_id TEXT NOT NULL PRIMARY KEY,
                run_id TEXT NOT NULL REFERENCES adaptive_question_contexts(run_id),
                attempt_id TEXT NOT NULL REFERENCES investigation_attempts(attempt_id),
                role TEXT NOT NULL CHECK(role IN ('investigator','skeptic')),
                ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 1 AND 6),
                phase TEXT NOT NULL CHECK(phase IN ('initial','follow-up')),
                capability TEXT NOT NULL,
                target_node_id TEXT NOT NULL,
                gap_id TEXT,
                gap_kind TEXT,
                purpose TEXT NOT NULL CHECK(length(purpose) BETWEEN 1 AND 600),
                request_json TEXT NOT NULL CHECK(length(request_json) BETWEEN 1 AND 8192),
                response_json TEXT NOT NULL CHECK(length(response_json) BETWEEN 1 AND 65536),
                response_sha256 TEXT NOT NULL CHECK(length(response_sha256) = 64),
                evidence_links_json TEXT NOT NULL CHECK(length(evidence_links_json) BETWEEN 1 AND 65536),
                evidence_links_sha256 TEXT NOT NULL CHECK(length(evidence_links_sha256) = 64),
                new_item_count INTEGER NOT NULL CHECK(new_item_count BETWEEN 0 AND 48),
                ownership_epoch INTEGER NOT NULL CHECK(ownership_epoch >= 1),
                created_at_unix_ms INTEGER NOT NULL,
                content_sha256 TEXT NOT NULL CHECK(length(content_sha256) = 64),
                UNIQUE(attempt_id, role, ordinal)
            );
            CREATE INDEX ix_adaptive_receipts_by_run_role
                ON adaptive_evidence_receipts(run_id,role,attempt_id,ordinal);
            CREATE TRIGGER adaptive_evidence_receipts_no_update
                BEFORE UPDATE ON adaptive_evidence_receipts BEGIN
                SELECT RAISE(ABORT, 'adaptive evidence receipts are immutable');
            END;
            CREATE TRIGGER adaptive_evidence_receipts_no_delete
                BEFORE DELETE ON adaptive_evidence_receipts BEGIN
                SELECT RAISE(ABORT, 'adaptive evidence receipts are immutable');
            END;

            CREATE TABLE adaptive_gap_events (
                event_id TEXT NOT NULL PRIMARY KEY,
                run_id TEXT NOT NULL REFERENCES adaptive_question_contexts(run_id),
                attempt_id TEXT NOT NULL REFERENCES investigation_attempts(attempt_id),
                role TEXT NOT NULL CHECK(role IN ('investigator','skeptic')),
                gap_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL CHECK(ordinal >= 1),
                event_kind TEXT NOT NULL CHECK(event_kind IN
                    ('proposed','allowed','denied','new-evidence','no-new-evidence',
                     'unavailable','budget-exhausted')),
                gap_kind TEXT NOT NULL,
                purpose TEXT NOT NULL CHECK(length(purpose) BETWEEN 1 AND 600),
                capability TEXT,
                receipt_id TEXT REFERENCES adaptive_evidence_receipts(receipt_id),
                diagnostic TEXT NOT NULL CHECK(length(diagnostic) BETWEEN 1 AND 256),
                ownership_epoch INTEGER NOT NULL CHECK(ownership_epoch >= 1),
                created_at_unix_ms INTEGER NOT NULL,
                content_sha256 TEXT NOT NULL CHECK(length(content_sha256) = 64),
                UNIQUE(attempt_id,role,gap_id,ordinal)
            );
            CREATE INDEX ix_adaptive_gaps_by_run
                ON adaptive_gap_events(run_id,attempt_id,role,gap_id,ordinal);
            CREATE TRIGGER adaptive_gap_events_no_update
                BEFORE UPDATE ON adaptive_gap_events BEGIN
                SELECT RAISE(ABORT, 'adaptive gap events are immutable');
            END;
            CREATE TRIGGER adaptive_gap_events_no_delete
                BEFORE DELETE ON adaptive_gap_events BEGIN
                SELECT RAISE(ABORT, 'adaptive gap events are immutable');
            END;
            PRAGMA user_version = 8;
            """);
        migration.ExecuteNonQuery();
    }
}
