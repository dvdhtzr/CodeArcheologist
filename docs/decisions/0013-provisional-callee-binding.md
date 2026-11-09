# ADR 0013 — Provisional source-local Roslyn callee bindings

- **Date:** 2026-11-09
- **Status:** Draft implementation; owner Windows and browser gates pending
- **Prerequisites:** ADRs 0007, 0010–0012 (reference-free type links, evidence graph, source declarations, observed invocation sites)
- **Architecture:** Approved Code Archeologist v0.2, C# analyzer and evidence integrity

## Problem

ADR 0012 introduced source-observed method invocation nodes and their
enclosing caller, but intentionally no callee links. A typed graph can be
more useful when a restricted Roslyn semantic model identifies a **single
captured method declaration** for a written invocation. However, applying
a normal project loader to arbitrary imported repositories may execute
MSBuild project targets or scripts. A synthetic reference-free
compilation cannot establish actual effective project dependencies,
overload resolution under real references, conditional compilation,
generated code or dynamic dispatch.

## Decision

Use an in-memory, reference-free `CSharpCompilation` containing **only
verified, saved C# file snapshots**. Do not load `.csproj` files into
Roslyn, add metadata references, restore packages, run analyzers or
generators, emit assemblies, execute scripts or invoke imported source
code. This matches the restricted, already established type-link
inspection strategy.

For each `InvocationExpressionSyntax` use
`SemanticModel.GetSymbolInfo(invocation)`. Emit a
`provisional-local` callee candidate **only if**:

1. Roslyn exposes a unique `IMethodSymbol` directly as `Symbol`;
   `CandidateSymbols` are **never** used to guess.
2. The method kind is `Ordinary`, not delegate invocation, constructor,
   local function or another unsupported kind. Reduced extension methods
   are excluded from this slice.
3. `OriginalDefinition.DeclaringSyntaxReferences` contains **exactly
   one declaration**.
4. That declaration is a `MethodDeclarationSyntax` in the current,
   manifest-verified captured file set. Its exact SHA-256 hash, source
   line/column, `SpanStart` and `SpanLength` are recorded.

Otherwise, return `unresolved` with a limitation/reason and **null
target**. The same-named method in the snapshot must not be used as a
fallback; neither an argument count nor a containing class name proves
which method a real project would call.

## Projection

Extend the existing graph, version
`csharp-snapshot-evidence-graph-v4`. It independently receives the
previous source-observed invocation-site report and the new
`roslyn-synthetic-callees-v1` report, and verifies exact workspace,
repository, snapshot and manifest fingerprint. Each binding must map
**bijectively** onto an observed invocation occurrence using
`path + file SHA-256 + span start + span length`; omission, duplication
or mismatch fails HTTP 409 without returning a partial graph.

For a successful provisional binding, the graph creates a directed
`candidate-callee` edge from the observed `invocation-site` node to
the already-observed method syntax declaration, with
`status=provisional-local`, evidence path, source hash and invocation
line/column. It never creates an imaginary declaration node when the
target does not match an existing captured syntax declaration. For
unresolved bindings, **no outgoing callee edge exists**.

The observation edge from the method/constructor to the invocation site
remains `contains-invocation` / `observed-syntax`, where supported.
These two statuses must not be conflated: caller containment is observed
syntactic structure; the callee association is a candidate under the
synthetic compilation only.

The browser shows counts for provisional and unresolved callees and
updates the invocation-node kind filter to
**Invocation sites (candidate or unresolved)**. Graph traversal may
show candidate method targets but never label them compiler-verified
dependencies, executed calls, or a complete method call graph.

## Bounds and safety

- The new binder enforces 250 files / 8 MiB of C# source and 4,500
  invocation sites, failing 413 rather than returning incomplete links.
  Existing snapshot verification and graph limits still apply.
- All snapshot files, including non-C# content, must pass manifest
  verification. Tampered file hashes fail 409; invalid UTF-8 fails 422.
- Only in-memory Roslyn syntax trees and a synthetic semantic model
  are constructed. No project evaluation, package restore, source
  execution, code generation, external network calls or disk graph
  writes are added.
- The synthetic compilation uses default parse options and no metadata
  references. Its selected method is **not** a confirmed real project
  binding, even if the same method appears to be in the same project
  under static membership heuristics.
- No transitive impact inference, virtual/interface dispatch
  verification, DI inference, reflection, partial method resolution,
  cross-revision identity or cross-repository call graph is claimed.
- Source evidence anchors are stable in one captured snapshot, not
  globally stable across edits.

## Acceptance

- Unique direct `Poke()` in a disposable fixture may produce a
  `candidate-callee` / `provisional-local` edge to the exact
  captured `Poke` declaration node, with matching path, SHA-256
  and line/column.
- `ArcheologistNoSuchMethodForFixture()` remains unresolved,
  with no fabricated target declaration or outgoing edge.
- Existing method-overload, nested lambda and local-function
  **caller containment** tests still pass.
- Graph counts for provisional and unresolved invocations partition
  every observed invocation site; all endpoints exist.
- Snapshot tamper, authentication, project XML DTD, size/usage limits,
  live source edits and restart stability remain covered.
- Owner-reported Windows `scripts/check.ps1` GREEN and browser
  acceptance are mandatory before merge.

This ADR extends ADR 0012's deliberate no-callee boundary with a
**source-local candidate** category. Nothing in ADR 0012's caution
about the absence of project-verified call relationships is relaxed.
