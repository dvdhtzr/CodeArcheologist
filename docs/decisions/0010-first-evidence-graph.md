# ADR 0010 — Bounded, navigable C# snapshot evidence graph

- **Date:** 2026-11-09
- **Status:** Draft implementation; owner-run Windows and browser verification pending
- **Prerequisites:** ADRs 0004–0009, especially verified saved snapshot artifacts, provisional Roslyn type uses and candidate-only .csproj membership
- **Architecture:** Approved Code Archeologist v0.2 — typed evidence, provenance and explicit uncertainty

## Decision

Provide a **deterministic, read-only graph projection** for one previously
captured repository snapshot, not a persistent graph database or verified
compiler dependency graph. It is the first navigable architecture-oriented
representation of existing, independently qualified observations.

Authenticated route:

`GET /api/v1/workspaces/{w}/repositories/{r}/snapshots/{s}/csharp-evidence-graph`

The graph is built in memory on demand after re-verifying copied source
artifacts. It never reopens the mutable registered repository. It delegates
to the existing bounded **project/link context** and **static C# project
membership** analyzers; checks both responses' exact workspace/repository/
snapshot/fingerprint provenance against the saved manifest; and fails closed
if either analyzer cannot produce complete evidence.

### Node types

| Kind | What it represents | Evidence |
| --- | --- | --- |
| `project-file` | Captured .csproj file; **not an evaluated project** | Snapshot path + SHA-256 |
| `csharp-file` | Captured .cs source file | Snapshot path + SHA-256 |
| `target-declaration` | Captured source location of a *candidate* Roslyn type target | Source path + SHA-256 + declaration line/column |
| `unresolved-use` | Source occurrence of a type spelling without a unique bound target | Source path + SHA-256 + usage line/column |

This is **not** a complete source-symbol graph. Declaration nodes are
created only for targets mentioned by the existing type-use inspection.
Unresolved occurrences do not create imaginary declaration nodes.

### Edge types and confidence

| Kind | From → To | Status |
| --- | --- | --- |
| `candidate-compile-item` | project-file → csharp-file | `candidate` |
| `declared-in` | target-declaration → csharp-file | `observed-location` (only a source location) |
| `base-type` or `object-creation` | csharp-file → target-declaration | `provisional-local` |
| `base-type` or `object-creation` | csharp-file → unresolved-use occurrence | `unresolved` |

Every edge exposes the captured evidence path/hash, its kind/status,
optional line/column and explanatory reason; C# type uses also preserve
the prior `shared-project-candidate`, `disjoint-project-candidates`,
`unknown-project-candidate` or `unresolved-target` project context.

**No edge is called `verified-dependency` or `compiled`.** The
`observed-location` status certifies neither effective compilation nor
binding accuracy; the exact saved text/declaration location is observed.
Even a shared project candidate is not a verified compile membership.

## Identity, stability, navigation

- All IDs are stable strings derived from exact snapshot-relative paths,
  hashes and syntax spans; IDs are unambiguous only **within the report's
  snapshot ID**.
- Node/edge arrays are sorted ordinally by ID, with duplicate/conflicting
  IDs rejected.
- All directed edge endpoints must refer to existing nodes. A missing
  endpoint is a failed analysis (409), not silently skipped.
- An authenticated browser **Explore C# evidence graph** view supports
  searching/filtering node kinds, selecting a node, seeing up to eight
  nearby nodes in a bounded overview diagram, and following individual
  connected edges with their evidence details.
- The API returns the complete bounded graph, while the UI deliberately
  limits visible search results/neighbors and states those display limits.
- Graph nodes and edges reconstruct identically from the same saved
  snapshot after an API restart and are unaffected by live source edits.

## Safety and failure limits

- The two upstream analyzers already cap C# files, byte counts, project XML
  files/bytes and type-use counts. Their 413/422/409 errors propagate.
- This projection further caps **10,000 nodes and 16,000 edges** per request,
  failing HTTP 413, never returning a truncated success payload.
- Static project XML remains DTD-prohibited and entity-free.
- No MSBuild evaluation, builds, restore, plugins, LLM calls, imported
  analyzers, source generators, compilation emission, Git invocation,
  execution of source code or network disclosure is added.
- No SQLite migration or persistent graph write is introduced in this PR.
  Saved snapshots already persist, so the graph is reconstructed on demand.
- Existing filesystem path-race and unencrypted-at-rest limitations remain.

## Explicit non-goals

No real solution/project evaluation, trusted reference-assembly binding,
method-call graph, DI graph, external package dependencies, data flow,
semantic change-impact propagation, graph search service, or Obsidian
knowledge vault mutation. A graph visualization cannot promote the quality
of its underlying observations.

## Acceptance

Disposable HTTP tests cover deterministic IDs and ordering, referential
integrity, all four node types, candidate/provisional/unresolved edge
statuses, exact saved project/source/target SHA-256 hashes, scope and
authentication failures, upstream 413 size gate, saved file/manifest
tamper 409, unsafe project XML 422, live source edits and app restart.
Browser acceptance must confirm search, selection, neighborhood browsing
and clear confidence labeling.
