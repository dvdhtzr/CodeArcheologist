# ADR 0014 — Static project candidate context for provisional callees

- **Date:** 2026-11-09
- **Status:** Draft; owner Windows and browser acceptance pending
- **Prerequisites:** ADR 0008 (static .csproj candidates), ADR 0009 (type-link context), ADR 0013 (source-local callee binding)
- **Architecture:** Approved Code Archeologist v0.2; evidence integrity and source-execution restrictions

## Motivation

PR #15 added method invocation → candidate source method edges using a
reference-free, synthetic Roslyn semantic model. Those edges are still
`provisional-local`. Without project context, a user cannot distinguish
a candidate whose source and target appear in the same statically
inspected project from one with currently disjoint or unknown project
candidates.

This evidence must be presented without mistaking an unevaluated
`.csproj` hypothesis for effective MSBuild compilation or an actual
method dependency.

## Decision

Reuse the existing `StaticCSharpProjectInspector` result already computed
and verified for graph construction. Do not add another project parser,
invoke MSBuild, construct a real compilation or re-read the registered
mutable repository.

For each existing `candidate-callee` / `provisional-local` edge,
cross-check the invocation file and candidate target method file against
the static project membership report. Classify one of:

| Status | Meaning |
| --- | --- |
| `shared-project-candidate` | Both files have at least one candidate project with identical saved project path and SHA-256 |
| `disjoint-project-candidates` | Both sides have candidates, but their candidate project sets do not overlap |
| `unknown-project-candidate` | One or both files have no static project candidate |

No `unresolved-target` callee edge is emitted: a missing/ambiguous
method target remains unresolved in the binder and has no outgoing
candidate-callee edge at all.

Statuses **do not affect the original `provisional-local` label**.
Shared candidates do not prove the call compiles; disjoint candidates do
not prove the call is impossible, since `ProjectReference`, imports,
conditional compile items and effective project configuration are not
evaluated. Unknown does not mean a source file is unused.

## Evidence contract

Extend `EvidenceGraphEdge` with optional
`sourceProjectCandidates`, `targetProjectCandidates` and
`sharedProjectCandidates` arrays, populated only on
`candidate-callee` edges. Each item carries:

- Captured `.csproj` path and SHA-256
- Static inclusion basis (literal Compile Include / inferred SDK default)
- Count of unresolved project rules

These are the same candidate records produced by the existing
`StaticCSharpProjectInspector` and used for type-link project context.
Their provenance is not upgraded. Non-callee edges have null arrays,
not fabricated project evidence.

The graph version becomes `csharp-snapshot-evidence-graph-v5`. Report
summary fields `calleeSharedCandidateCount`,
`calleeDisjointCandidateCount`, and `calleeUnknownProjectCount` count
**provisional callee edges**, not all invocation sites, and must
partition `provisionalCalleeCount`. `unresolvedCalleeCount` remains
separate, with no inferred project context.

The existing browser graph explorer displays these three counts.
Selecting a `candidate-callee` edge shows status, explanation and
source/target/shared candidate projects with individual SHA-256,
inclusion basis and unresolved-rule count. Search, navigation, and all
existing evidence anchors remain unchanged.

Browser discoverability: a dedicated **Find callee project evidence**
section lists the actual candidate edges before the generic graph-node
picker. It supports filtering to all/shared/disjoint/unknown classifications
and expandable source/target/shared project details. If no candidate edges
exist in the selected saved snapshot, it explicitly explains that there are
no matching callee project classifications to inspect. This is not a
compilation, execution or project-membership verdict. Each list is
bounded to its first 25 matching links with the total count disclosed.

## Limits, exclusions and integrity

- No additional I/O, NuGet package, model, network call, build or source
  execution is introduced; only the existing saved-project report is
  reused.
- All independent analyzers must match the requested snapshot ID,
  workspace, repository and manifest fingerprint.
- The existing project inspection prohibits DTD/entity processing,
  limits project count/bytes and rejects unsafe XML; other analyzer and
  graph-size caps are unchanged. Fail closed rather than returning
  partial success.
- A project's candidacy is stored alongside its *copied project file
  hash*, never inferred from Git state or live source changes.
- No actual project dependency, method call, runtime behavior or
  project compilation is marked verified. Project references remain
  unexamined.

## Acceptance

Disposable snapshot fixtures require:

1. A project-local call to a target in a literal-included shared file:
   `shared-project-candidate` with exact project path, hash and
   inclusion basis.
2. An invocation from a second project with candidate files not
   overlapping the target project: `disjoint-project-candidates`.
3. An invocation in an unmapped source file:
   `unknown-project-candidate`.
4. Unresolved callee sites have no invented target/project edge.
5. Counts partition provisional callees exactly.
6. Existing auth, snapshot/file tamper rejection, source-edit stability,
   restart determinism, unsafe XML 422 and all limits still pass.
7. Owner Windows check GREEN, followed by browser confirmation of
   the three classifications and evidence details, before merge.

This is evidence enrichment of a provisional call graph, **not**
project-aware binding or a quality score.
