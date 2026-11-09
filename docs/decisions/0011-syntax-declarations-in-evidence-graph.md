# ADR 0011 — Expand the evidence graph with syntax-observed C# declarations

- **Date:** 2026-11-09
- **Status:** Draft; local Windows verification and browser acceptance pending
- **Prerequisites:** ADR 0006 (syntax-only Roslyn), ADRs 0007–0010 (type links, static projects, context, evidence graph)
- **Architecture:** Approved Code Archeologist v0.2 — syntax and symbols, stable revision-scoped graph identity, evidence class separation

## Motivation

PR #12's graph only created declaration nodes that were targets of the
provisional synthetic Roslyn type-link analyzer. A method with no inspected
base-type or explicit `new` relationship was invisible. That was an honest
limitation, but it made the explorer poor for understanding what a codebase
contains.

PR #8 already supplies a bounded, non-executing Roslyn syntax inspector.
Use that existing, exact-snapshot source observation to expand the graph
before tackling source-to-source method calls or real project compilation.

## Decision

Update the existing authenticated, snapshot-scoped
`/csharp-evidence-graph` endpoint, rather than adding another endpoint
or storage representation. Graph version becomes
`csharp-snapshot-evidence-graph-v2`, with
`syntaxAnalyzerVersion` and `syntaxDeclarationCount`.

Invoke the existing `CSharpSyntaxInspector` on the same snapshot as the
type-link context and project candidate inspectors. Reject mismatched
workspace/repository/snapshot IDs or fingerprint SHA-256 as a conflict,
without returning partial data.

Each supported syntax declaration receives a node keyed by exact captured
file path, **file SHA-256**, `SpanStart` and `SpanLength`, with an ordinally
sorted graph representation. The syntax inspector provides these C# kinds:
namespace, record, class, struct, interface, enum, delegate, method,
constructor and property.

New nodes use kind `syntax-declaration`; they retain a qualified display
name, source file location and an explanation of their syntactic (not
compiler-verified) nature. Two overloaded methods with the same spelling
are separate nodes because their saved source spans differ; no signature
or compiler symbol identity is fabricated.

For each declaration, add a directed `declared-in` edge to the exact saved
`csharp-file` node, status `observed-location`. This status asserts
only that a syntactic declaration exists at that saved source location.

## Reuse and coexistence with candidate type targets

Both the prior `target-declaration` node and a syntax-observed declaration
represent the same saved location when their file path, file hash and
source-span identity are equal. **Reuse the existing target node and its
`declared-in` edge in that case**, not a duplicate syntax node.

The target retains its original kind, label and provisional type-use
relationships; a matching syntax declaration does not promote Roslyn's
synthetic binding to a verified project dependency.

When a syntax declaration is not targeted by an inspected type use, add
it independently as `syntax-declaration`. Parsed declarations with no
project candidate remain in the graph; missing project candidates are not
evidence of exclusion.

## Trust boundaries and limitations

- No imported code, analyzers, source generators, project evaluation,
  MSBuild targets, restore, build scripts or arbitrary binaries are run.
- The syntax inspector uses `LanguageVersion.Preview` with no project-specific
  preprocessor symbols and may omit inactive conditional branches.
- Names and containment are **syntax-derived**; nested types, overloads,
  partial types and compiler symbol identity remain unresolved.
- File hash and source span are stable within a snapshot, not universal
  symbol IDs across refactors or revisions.
- The new graph remains an **on-demand, in-memory projection**, not the
  future SQLite-backed durable graph/evidence ledger.
- No direct method-call relationship, DI edge or impact claim is made.
- Existing 10,000-node/16,000-edge limits and upstream syntax/type/project
  caps continue to fail HTTP 413 rather than return incomplete successes.
- The snapshot manifest and every file are hash-verified by the existing
  analyzers. Changed bytes must be rejected (409); unsafe XML is rejected
  by the static .csproj inspector (422).
- No GraphQL, cloud dependency, new NuGet package, CI pipeline or database
  migration is added in this slice.

## Acceptance

- Existing PR #12 graph nodes, links and statuses still function.
- Unreferenced syntactic class, nested class, method overloads and property
  declarations appear as exact-span source nodes, with `declared-in` links.
- Existing linked target declarations do **not** create duplicate nodes or
  `declared-in` edges.
- Overloaded methods share display names but **not identities**.
- Node and edge IDs, ordering and SHA-256 anchors remain deterministic
  on repeat calls, live source edits and API restart.
- Graph UI adds a filter for syntax-only declarations and a count but does
  not label observed syntax as verified compilation.
- Owner confirms Windows `scripts/check.ps1` GREEN and browser acceptance
  before the PR can be merged.

Method-call analysis and verified project-aware binding remain separately
scoped future milestones with independent evidence requirements.
