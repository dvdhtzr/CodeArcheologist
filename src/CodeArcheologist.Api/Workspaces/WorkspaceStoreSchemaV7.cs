using Microsoft.Data.Sqlite;

namespace CodeArcheologist.Api.Workspaces;

/// <summary>Forward-only metadata migration. Legacy Gate C runs remain unmodified
/// and have no question-context row; the C6b execution schema is untouched.</summary>
public sealed partial class WorkspaceStore
{
    private static void MigrateToV7(SqliteConnection db, SqliteTransaction tx)
    {
        using var migration = Command(db, tx, """
            CREATE TABLE investigation_question_contexts (
                run_id TEXT NOT NULL PRIMARY KEY REFERENCES investigation_reports(run_id),
                question_id TEXT NOT NULL UNIQUE,
                contract_version TEXT NOT NULL
                    CHECK(contract_version = 'r3-investigation-context-v1'),
                subject_node_id TEXT NOT NULL,
                subject_node_kind TEXT NOT NULL
                    CHECK(subject_node_kind IN ('http-endpoint-attribute','http-minimal-mapping')),
                question_kind TEXT NOT NULL CHECK(question_kind = 'explain-http-entry-point'),
                context_json TEXT NOT NULL CHECK(length(context_json) BETWEEN 1 AND 8192),
                context_digest TEXT NOT NULL CHECK(length(context_digest) = 64),
                created_at_unix_ms INTEGER NOT NULL
            );
            CREATE TRIGGER investigation_question_contexts_no_update
                BEFORE UPDATE ON investigation_question_contexts BEGIN
                SELECT RAISE(ABORT, 'question contexts are immutable');
            END;
            CREATE TRIGGER investigation_question_contexts_no_delete
                BEFORE DELETE ON investigation_question_contexts BEGIN
                SELECT RAISE(ABORT, 'question contexts are immutable');
            END;
            PRAGMA user_version = 7;
            """);
        migration.ExecuteNonQuery();
    }
}
