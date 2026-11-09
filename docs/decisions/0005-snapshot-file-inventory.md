# ADR 0005 — Deterministic, snapshot-scoped file inventory

- **Date:** 2026-11-09
- **Status:** Implemented in draft PR; local verification pending
- **Depends on:** ADR 0004 explicit read-only snapshots, SHA-256 manifest/byte verification
- **Approved design:** Code Archeologist v0.2, §7 ingestion inventory and §8 polyglot analyzer contracts

## Decision

Implement the first deterministic analysis capability as a **filename-only
inventory** of one already captured, immutable-by-app snapshot. All observed
paths, file sizes, SHA-256 hashes, and exclusions come from the stored
manifest. The inventory is computed on demand after the existing
`SnapshotCaptureService.ReadManifest` validates the manifest and every copied
file's hash. It never reads the mutable registered source directory.

An authenticated, workspace/repository/snapshot-scoped endpoint returns:

`GET /api/v1/workspaces/{workspaceId}/repositories/{repositoryId}/snapshots/{snapshotId}/inventory`

The response includes snapshot and workspace IDs, original fingerprint,
`manifest-inventory-v1` analyzer version, detection method
`path-and-extension-only`, included file counts/bytes, excluded entry count,
stable file records, deterministic per-language and per-role summaries, and
explicit limitations.

Each included file retains `path`, `sha256`, `sizeBytes`, probable `language`,
and guessed `role`. Supported filename patterns include C#/.NET,
TypeScript/React, JavaScript, Python, Java, Go, Rust, C/C++, SQL, configuration,
documentation, project and dependency manifest candidates, and tests. A file
can be marked `Unknown` and/or `unclassified` without being treated as an
error. Candidate roles are **not verified semantic findings**.

No LLM or Microsoft Agent Framework is used here; no cloud requests, Git,
package restores, project loaders, build targets, compiler plugins, source
scripts or imported code execution occur. No SQLite migration or new mutable
persistence is required: inventory is rebuildable from verified snapshot
content and versioned detector rules.

## Evidence and trust boundaries

A path and its digest are direct evidence of a file *in that captured
snapshot*. Language and role labels are hypotheses based solely on filename
suffixes and directory names. This operation does not prove a file's syntax,
ownership, functionality, dependencies, entry points or behavior. It does not
inspect excluded subtrees or search included files for credentials. The
browser must show the heuristic nature of the classifications.

The endpoint is read-only and requires the existing paired browser session.
Invalid workspace/repository/snapshot combinations return 404. If the
snapshot manifest or included bytes are modified, its integrity verification
returns a conflict rather than an inventory with fabricated verified status.
An earlier snapshot's classification must not silently follow newer working
tree changes.

Future semantic analyzers must add their own extraction method, evidence
anchors, uncertainty and analyzer version; they must never promote these
filename-only guesses to verified structural claims.

## Verification

Extend the existing disposable snapshot integration suite to verify:
authenticated access, correct workspace/repository/snapshot scoping, exact
file hash/path provenance, deterministic ordering, role/language summaries,
explicit exclusions and limitations, saved-byte/manifest tamper rejection,
and restart persistence. Preserve the full older test suite.

A successful run is **not** proof of deep C# semantic analysis, graph
relations, full source ingestion, Git revision identity, secrets detection,
cross-platform packaged deployment or backup correctness.
