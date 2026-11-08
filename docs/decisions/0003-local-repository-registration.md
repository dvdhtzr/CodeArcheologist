# ADR 0003 — Register existing local repository roots without ingestion

- **Date:** 2026-11-08
- **Status:** Implemented on a draft branch; local verification pending
- **Applies to:** Foundation milestone following SQLite workspace persistence
- **Architecture baseline:** Multi-repository workspaces, ADR-007 safe source handling,
  ADR-011 immutable snapshot provenance

## Scope and user consent

A user who has paired their browser may explicitly enter the **absolute path**
to an existing local directory and register it within a chosen workspace.
This operation creates only a durable `RepositorySource` metadata record;
the app does **not** enumerate source files, read project files or `.git`
contents, launch `git`, execute imported code, invoke MSBuild or package
managers, clone, copy files, create snapshots or send data to a model.

The first implementation requires an explicitly pasted/typed path. Browser
file-picker and drag-and-drop are deferred; web browsers typically do not
expose arbitrary machine-local absolute paths to web applications.

There is no background watcher and no automatic import. Registration is not
an assurance that the folder's contents are safe, consistent or trusted.

## Path and scope policy

- Only existing, accessible, fully qualified local **directories**.
- Reject filesystem roots; relative, dot and parent-directory path segments;
  control characters; empty paths; paths longer than 2,048 characters.
- On Windows reject UNC, device and network share path prefixes.
- Reject symbolic links, junctions and reparse points along the selected path's
  full directory ancestry, and the application's own database directory.
- Normalize directory separators via `Path.GetFullPath`, trim trailing
  separators and use an OS-sensitive key for uniqueness (case-fold on Windows).
  This is not equivalent to resolving all filesystem identities or hard links.
- Classify as `git` if the selected root has a `.git` marker (file or
  directory), otherwise as `folder`. Only the marker's presence is checked.

**Important limitation:** validation occurs when the user registers the
location. The filesystem may change afterward (e.g. symlink replacement).
This is not a secure snapshot sandbox. Future ingestion must acquire content
into separately controlled immutable storage, re-check every path and symlink
during file traversal, block path escape and malicious inputs, enforce byte
and depth budgets, detect in-flight changes, and record immutable content
fingerprints with exclusions.

## Persistence and API

Schema v2 transactionally extends SQLite schema v1, preserving existing
workspaces. New `repository_sources` records contain: UUID, parent workspace
UUID (foreign key), absolute local path, normalized uniqueness key, source
kind (`git` or `folder`), UTC registration time, version 1, idempotency key
and request digest. Different workspaces may register the same source. Within
one workspace, duplicate paths are rejected to avoid confusing aliases.

All endpoints require a paired browser session:
- `GET /api/v1/workspaces/{workspaceId}/repositories` returns the most recent
  100 registrations and a total count, or 404 for an unknown workspace.
- `GET /api/v1/workspaces/{workspaceId}/repositories/{repositoryId}`
  returns one registration scoped to the workspace, or 404.
- `POST /api/v1/workspaces/{workspaceId}/repositories` takes body
  `{"localPath":"<absolute local folder>"}`, UUID `Idempotency-Key` header,
  application request header, and valid session-bound CSRF header. New = 201,
  same request/key replay = 200, invalid path/key = 400, unknown workspace =
  404, duplicate path or key replay with different content = 409.

No edit/delete endpoint yet. Any later mutation will require version-based
optimistic concurrency, a reviewed impact on persisted snapshots, and explicit
user consent.

This schema records a *mutable locator*, not a resolved repository revision.
Stable source IDs and eventual immutable snapshot IDs must never be conflated.
Paths may contain personally identifying names and are stored as local plaintext
metadata; users should not register sensitive directory names.

## Verification

Local integration tests use an isolated temporary SQLite database and
deliberately constructed local source folders. They assert authentication,
CSRF/origin denials, invalid and linked paths, duplicate and idempotent
requests, workspace containment, multi-repo registrations, source tree
unchanged, process restart survival and previous-session rejection.
The check script runs the suite after the existing security/workspace tests.

A passing suite does not prove full filesystem race safety, remote-hosting
security, cross-platform package correctness, immutable provenance, or
safe source ingestion. These must be gated in later PRs.
