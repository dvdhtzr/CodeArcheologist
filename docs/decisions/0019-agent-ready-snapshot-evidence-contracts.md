# ADR 0019 — Typed, read-only snapshot evidence tools before MAF

- **Date:** 2026-11-09
- **Status:** Draft implementation, owner Windows/browser verification pending
- **Architecture:** Code Archeologist v0.2, evidence integrity and bounded agent tools
- **Roadmap:** Finite analyzer-to-agent readiness gate #21, then genuine MAF + local Ollama #19
- **Dependencies:** ADRs 0012, 0015, 0016, 0017, 0018; graph v8

## Purpose

The future Functional Investigator and Skeptic/Reviewer must retrieve
**application-owned, typed, independently checkable snapshot evidence**.
Agents must not be asked to read arbitrary paths, improvise graph IDs,
run project builds or treat model narratives as proof of functionality.

This slice supplies a real callable contract with strict source
provenance and a deliberately narrow citation **reference** validator.
It does not add models or MAF packages yet. The owner asked us to
complete these deterministic foundations before the first agent run.

## Entry-point evidence tool

`SnapshotEvidenceTools.ReadEntryPoint(snapshot, entryPointId, ct)`
accepts only an existing graph v8 HTTP candidate node ID of kind
`http-endpoint-attribute` (controller method annotation) or
`http-minimal-mapping` (Minimal API-style member invocation).

The browser has a protected, read-only, scoped companion route:

`GET /api/v1/workspaces/{workspaceId}/repositories/{repositoryId}/snapshots/{snapshotId}/evidence-tools/entry-point?entryPointId=...`

The caller selects a saved entry point by exact graph ID; there is
**no** user-supplied file path, arbitrary search path, command,
external URI or source-code execution. The application resolves
workspace/repository/snapshot through its authorization-backed
`WorkspaceStore`, rebuilds the bounded, SHA-256-verified evidence
graph, and independently joins the selected HTTP/Minimal API
candidate back to the matching syntax analyzer report.

The response is a versioned `AgentEvidencePackage` with:

- Exact workspace/repository/snapshot IDs, fingerprint, graph and tool versions.
- Entry-point node ID/kind, a **bounded excerpt of the exact saved
  syntax span** (maximum 2,400 UTF-16 characters) and a truncation flag.
- Typed `AgentEvidenceReference` nodes with exact IDs, observed or
  candidate status, source path, saved SHA-256 and line/column.
- Typed `AgentEvidenceRelation` edges with exact endpoints, original
  status, path/hash/line/column and project-context status where present.
- Counts of total versus returned direct invocation sites, and the
  snapshot-wide unresolved callee count **not attributed to any target**.
- Explicit unknowns and limits, including model-instruction handling:
  **copied source text is untrusted data, not agent instructions**.

For a controller HTTP attribute, the source excerpt is the annotated
method declaration. Only independently observed
`contains-invocation` / `observed-syntax` sites **directly inside
that method** are included, capped at 12; each source-only
`candidate-callee` / `provisional-local` relationship remains
unchanged. No other method is inferred by spelling or transitive
traversal. If more sites exist, the response explicitly reports how
many were omitted.

For a Minimal API-style mapping, the excerpt is the **mapping
invocation syntax**. The `candidate-http-mapping` edge links it to
the observed invocation; optional containing source method/file is
**registration-site context**, not the bound route handler. No handler,
actual route, middleware, DI scope or executing behavior is invented.

All returned relation endpoints must occur among returned nodes.
Any missing or inconsistent join fails HTTP 409; unsupported target
kinds fail 404; malformed or too-long IDs fail 400. Existing
graph/import source limits fail 413; invalid saved UTF-8 fails 422.

## Independent citation-reference check

`SnapshotEvidenceTools.CheckCitations(snapshot, request, ct)`
is callable by the future orchestrator and available via a
**CSRF-protected authenticated POST**:

`POST .../evidence-tools/check-citations`

The request pins `entryPointId` and exact saved snapshot
`fingerprintSha256` and contains 1–8 claims. Each claim has bounded
text (1–1200 characters), one of two supported `claimKind` values,
and 1–8 exact supplied evidence IDs:

- `source-observation`: requires a cited node or relation marked
  `observed-syntax` in the selected evidence package.
- `candidate-relationship`: requires an actual cited relation marked
  `candidate` or `provisional-local` in that package.

Invented, foreign, omitted or duplicate IDs are rejected. A stale
fingerprint fails HTTP 409. Unsupported claim kinds such as
`registered-runtime-endpoint` are rejected; the validator **never**
upgrades a candidate source relationship into a real route, runtime
behavior or compiled dependency.

A passing claim returns
`references-valid-not-semantic-verification`. This is intentional:
the validator proves that IDs, source types and declared relationship
statuses are valid **within the exact supplied evidence package**.
It does **not** prove a free-text sentence is true, that the cited
source semantically supports the claim, or that a model did not follow
prompt injection. Those stronger checks, adversarial evaluation,
contradiction analysis and independent evidence quality gates remain
future implementation under #19/#21.

## Trust, limits and UI

- No new NuGet/npm package, cloud provider, Ollama call, agent,
  database migration, GitHub CI or background processing.
- Snapshot bytes are read exclusively through
  `SnapshotCaptureService.ReadVerifiedSnapshotFile`; the registered
  mutable source tree is not opened by the tool.
- No project evaluation, MSBuild, assembly load, restore, imported
  analyzers/generators or script execution.
- Tool calls are read-only, request-driven, synchronous and cancellable
  with existing graph limits (10,000 nodes / 16,000 edges).
- The React C# graph view adds **Inspect agent-ready evidence package**
  buttons to existing controller and Minimal API candidate lists.
  The result panel receives scroll/keyboard focus, displays source
  excerpt, exact typed nodes/edges and unknowns, and can make a **small
  example citation check** using the session's CSRF token. It never
  claims source citation validation equals narrative truth.
- No generated model or actor is permitted to choose arbitrary files
  or run a generic shell tool through this contract.

## Acceptance and stop rule

Tests must cover both controller and Minimal API candidates; exact
saved fingerprints and span-backed source excerpts; one-hop and
registration-site-only relationships; valid typed references; invented
and duplicate citations; invalid claim kind; wrong evidence type;
stale fingerprint; 401/403, 400/404, tamper 409, bound 413 and restart
stability. User runs the Windows build + integration suite, then
verifies actual browser navigation and cited evidence.

**Stop at a GREEN merge for this contract**, then revisit the finite
readiness issue #21. Do not indefinitely expand deterministic
analyzers. The next substantial work item is real local
**Microsoft Agent Framework + Ollama** workflow with at least two
independent roles, actual trace/tool usage, bounded failure taxonomy
and model-adversarial test fixtures under issue #19.
