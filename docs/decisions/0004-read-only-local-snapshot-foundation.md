# ADR 0004 — Explicit read-only snapshot acquisition

- **Date:** 2026-11-08
- **Status:** Draft implementation committed; owner-run verification pending
- **Scope:** First bounded local-folder capture and immutable application-managed copies
- **Architectural basis:** approved v0.2 §7 and C3.2, ADR-007 non-executing import,
  ADR-011 immutable source revision correctness, previous SQLite workspace decisions

## Why this boundary matters

A registered repository path is a **mutable locator**, not a source revision.
Only a captured manifest of content hashes and owned copied bytes may serve
as a source snapshot. Captures are requested deliberately by a paired local
browser; they never start automatically from registration or a watcher.

This milestone does not claim Git commit resolution, compiler analysis,
semantic understanding, graph evidence, background agents, or a complete
security sandbox. The scanner treats repository content as untrusted bytes
and never interprets source comments/README instructions as commands.

## Acquisition protocol v1

1. Require a paired session, CSRF token, fresh UUID idempotency key and an
   existing repository registration under the requested workspace.
2. Recheck the registered root and its link/junction ancestry. Reject missing,
   relative, linked or unsafe roots, and source roots containing the app data
   directory (avoid capturing our own snapshots/database).
3. Walk the source tree without executing imported tools or projects. Reject
   traversal beyond the registered root. Stop at a depth of 24, 5,000 included
   files, 8 MiB per file, 128 MiB included data or 25,000 visited entries.
   **If exceeded, fail the entire capture; do not report a partial tree as
   complete.**
4. Skip known vendor/build/cache directories, VCS metadata, known credential
   files, and known binary file extensions. Record each skipped entry as
   relative path plus exclusion reason. Directory exclusions represent whole
   subtrees; their nested contents are deliberately not enumerated.
5. Exclude detected symlinks/junctions at walk boundaries; never traverse them.
   Recheck each parent directory and file metadata before and after reading.
6. Stream eligible file bytes read-only into an isolated staging directory under
   per-user app data; hash each copy with SHA-256. Never write to the original
   repository. Refuse changes in file size or modification time.
7. Re-enumerate the source tree. If the included path/metadata inventory or
   the exclusion inventory differs, discard staging and retry once. If it
   still changes, reject without publishing a trusted record.
8. Build a policy-versioned **content fingerprint** from ordered relative
   paths, exact lengths, individual SHA-256 hashes and recorded exclusions.
   The fingerprint intentionally does not claim a Git commit identity.
9. For an unchanged content/exclusion fingerprint in the same registered
   source, reuse the existing snapshot ID; never append duplicate records
   merely because a user clicked capture again. Idempotent network retries
   return the original snapshot, even if the mutable source changes later.
10. Otherwise serialize a manifest with source ID, workspace ID, snapshot ID,
    policy, explicit non-Git revision kind, capture time, included-file hashes
    and exclusions. Hash the manifest, atomically move staging to a new
    app-managed snapshot directory, then record the identity and request key
    transactionally in SQLite schema v3.
11. Reading a manifest verifies the stored manifest SHA-256, all identity
    fields and the SHA-256/size of **every captured file**. A mismatch is an
    explicit conflict, never a silently "verified" result.

Snapshot bytes are under
`<app-data>/CodeArcheologist/snapshots/<snapshot-id>/files/...` and
`manifest.json` in the same snapshot folder. Identities in this path are
server-generated, not supplied as arbitrary filesystem paths. Tests redirect
the entire application data root to a disposable temporary folder.

## API and UI

- `GET /api/v1/workspaces/{w}/repositories/{r}/snapshots`: the latest 100
  records, total count and overflow flag. 404 for unknown repository scope.
- `GET .../snapshots/{snapshotId}`: scoped metadata or 404.
- `GET .../snapshots/{snapshotId}/manifest`: verifies manifest and copied
  bytes on each read; returns artifact paths/hashes/exclusions or an explicit
  conflict if tampered.
- `POST .../snapshots`: authenticated, CSRF-protected, bodyless, explicit
  capture action with a UUID `Idempotency-Key`. New = 201 + Location;
  existing/retry = 200; unsafe/inconsistent source = 409; over budget = 413;
  inaccessible source = 422; missing record = 404.

The browser has a manual `Capture read-only snapshot` control, local history,
and a manifest inspector. A successful capture does not trigger analyzers,
Git or model calls.

## Security limitations and follow-up requirements

- **No arbitrary source execution**, no `git`, `dotnet build`, `npm install`,
  project loading or compiler plugin invocation on imported material.
- **Not a race-free filesystem sandbox.** The base implementation performs
  path, link, metadata and inventory rechecks, but these are not atomic with
  opening OS file handles. A hostile concurrent process with write access
  may replace a path between checks. Future snapshot hardening must use
  platform-specific no-follow/openat-style handles or a separately
  restricted acquisition process and adversarial race fixtures before
  handling hostile content under stronger guarantees.
- This scans known exclusions, **not a secrets detector**. Private tokens
  embedded in ordinary source may still be captured. Stored bytes are local
  plaintext; neither app data nor the included files are encrypted by this
  feature. Do not capture sensitive repositories until exclusion and storage
  policies are acceptable for your machine.
- Source content is copied by bytes; source trust and proven semantic meaning
  are not established. A Git working tree's HEAD, branch and dirty state
  are not resolved. The manifest explicitly states a filesystem fingerprint.
- Files under ignored directories are not inventoried and changing them does
  not alter the fingerprint. No user-custom exclusions or `.gitignore`
  semantics are asserted yet. Unknown binary extensions may still be copied.
- Application-managed "immutable" means the application **never edits a
  published snapshot**; it is not OS-enforced WORM storage. Hash revalidation
  detects post-capture changes to stored content.
- A process crash between publishing snapshot files and SQLite commit may
  leave an unreferenced staging/published folder. A future recovery/garbage
  collection pass must reconcile these safely without deleting user work.
- No backup/restore, quotas, workspace export, multi-platform installer,
  timestamp-independent full source consistency proof, or access-control
  hardening beyond local OS-account permissions is claimed.

## Verification gate

The new local integration suite constructs temporary malicious project and
credential fixtures. It proves no build/agent execution is requested, checks
included and excluded file behavior, copy integrity, original-tree preservation,
fingerprint dedup, idempotency across restarts, failure on large files and
self-recursive data roots, and rejection of manifest or saved-byte tampering.

The earlier security, SQLite workspace, and local repository registration
suites remain required. **Do not mark this milestone GREEN until the owner
runs `scripts/check.ps1` and verifies the browser capture experience.**
