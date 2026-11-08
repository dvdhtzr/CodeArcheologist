# ADR 0002 — Durable workspace metadata, first SQLite slice

- **Status:** Implemented as proposed; local and cross-platform validation pending
- **Owner direction:** ADR-001 (SQLite), ADR-003 (localhost-first) and ADR-011 (stable identity)
- **Date:** November 8, 2026
- **Scope:** Workspace metadata only; no imported repositories or analysis

## Storage and schema

The process creates a SQLite database named `workspaces.sqlite3` under the
current user's operating-system local application data directory, in a
`CodeArcheologist` subfolder. No developer configuration or database server
is required. The testing-only `CODE_ARCHEOLOGIST_DATA_DIR` override directs
the test processes to isolated temporary directories; it is not needed for
normal use.

Schema v1 is tracked by SQLite `PRAGMA user_version`. Startup applies v1 in a
transaction, refuses databases with a newer schema version, and does not erase
existing records. The database uses WAL journaling and a five-second lock
wait. The native SQLite and packaging behavior still require real machine tests.

A workspace record has a generated stable UUID, a user-supplied display name,
created/updated UTC timestamps, and a version field starting at 1.
Workspace name is never used as a filesystem path. Each creation request is
bound to a UUID idempotency key and request digest in a unique indexed column.
Retries with the same key and same normalized name return the original
workspace; reuse of a key with different content is a conflict.

These are **metadata records**, not vaults, repository snapshots, evidence,
or permissions. The future workspace manifest, vault mapping, backups,
repository memberships and schema migration path will extend the model in
separate reviewed changes.

## API contract (v1)

All routes except `/api/v1/health` require an authenticated local browser
session, validated by the existing authentication middleware.

- `GET /api/v1/workspaces`: newest 100 records, with `totalCount` and
  `hasMore`. Pagination is explicitly deferred. This endpoint is read-only.
- `GET /api/v1/workspaces/{id}`: one UUID-scoped record, or 404.
- `POST /api/v1/workspaces`: body `{"name":"Example"}`, a required UUID
  `Idempotency-Key` header, authenticated cookie, application request header,
  and session-bound CSRF header. Created = 201 with Location; identical retry =
  200; mismatched key reuse = 409; invalid name or key = 400.
- Names are trimmed and limited to 120 printable UTF-16 code units; blank
  values and control characters are rejected.

No repository paths, source bytes, model output or arbitrary SQL are accepted.
Queries use parameterized commands. Future updates and deletes must use
optimistic concurrency (`expected_version`/`If-Match`) and their own
idempotency/authorization test cases.

## Test and operational boundaries

Local integration tests must create and later remove their own
temporary database, never the user's actual application data. The workspace
suite tests authentication and CSRF rejection, malicious Origins, validation,
idempotent retry, collision, addressability, SQL-like names as plain data,
server restart, and rejection of a previous process's session.

Passing tests will demonstrate basic persistence only. It will **not** certify
SQLite native packaging across OSes, backup consistency, crash durability,
ACL hardening, unlimited scalability, workspace export/restore, multi-repo
snapshot provenance, or a successful complete Phase 1 release.
