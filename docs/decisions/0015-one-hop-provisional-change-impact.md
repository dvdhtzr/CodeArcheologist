# ADR 0015 — One-hop provisional C# change-impact explorer

- **Date:** 2026-11-09
- **Status:** Draft; owner-run Windows and browser acceptance pending
- **Prerequisites:** ADR 0010 (snapshot evidence graph), 0012 (observed invocation containment), 0013 (provisional callee links), 0014 (project candidate context)
- **Architecture:** Approved Code Archeologist v0.2 — direct versus hypothetical impact, source evidence quality, and non-executing analysis

## Motivation

PR #16 made provisional method callee links and their source/target
project-candidate context discoverable in the graph. We can now answer
a tightly scoped change-impact question without inventing new bindings:
**given this captured C# method declaration, which saved invocation
sites have provisional links *to* it, and what source method or file
contains each invocation?**

This is a candidate review checklist, not proof of actual callers or
that any change would break them.

## Decision

Add an explicit **Explore direct change-impact candidates** panel to
the existing authenticated **C# evidence graph explorer**. It consumes
the same already scoped, on-demand, SHA-256-verified graph v5 response.
There is **no new endpoint, analyzer, graph edge kind, persisted impact
finding, background scan, or network call** in this slice.

The panel lists method declarations from the observed syntax inventory
plus targets of existing `candidate-callee` edges. Each method is
identified by the graph's exact saved file SHA-256 + syntax span, not
by a spelling match. The method finder supports text filtering by
method label or saved path, shows the source line and count of inbound
candidate call sites, and limits rendered matches to 25 with an explicit
count and search guidance. The initial selection favors an existing
candidate-backed target (if any).

For a selected method `M`:

1. Find *only direct incoming* edges with
   `kind=candidate-callee`, `status=provisional-local`,
   and `toId=M.id`.
2. For each edge, display its **observed invocation node**, exact
   saved path/hash/line/column, and the original source-only Roslyn
   candidate explanation.
3. Follow that invocation's existing **single**
   `contains-invocation` / `observed-syntax` edge in the reverse
   direction to an observed enclosing method/constructor declaration
   or, when unsupported, the containing captured C# file.
4. Display project-candidate context as the existing
   `shared-project-candidate`, `disjoint-project-candidates`, or
   `unknown-project-candidate` with the saved source/target/shared
   .csproj hashes and unresolved rules.
5. Provide direct navigation to the selected invocation in the
   existing graph explorer. The earlier callee evidence finder links
   to this impact panel for the chosen target method.

Count distinct directly enclosing methods/constructors separately from
file-scoped call sites and candidate invocation-site occurrences.
Do **not** infer an enclosing method from an unsupported lambda, local
function, accessor, initializer or top-level statement.

## Confidence and exclusions

- **Source containment** is `observed-syntax`, based on the saved file.
  **Callee binding** remains `provisional-local` under a reference-free
  synthetic Roslyn model. Shared project candidates never upgrade it.
- The panel does **not** classify any change as a breaking change,
  tested risk, compiled failure, verified dependency or executed call.
- **No transitive traversal**. If `Run()` provisionally calls `Call()`
  and `Call()` provisionally calls `Ping()`, selecting `Ping()`
  displays the `Call()` invocation site only, never `Run()` as a
  direct or automatically supported indirect impact.
- Unresolved calls cannot be linked to a target method and are excluded
  from per-method inbound candidate counts. Their snapshot-wide number
  is displayed with the explicit warning that it cannot be allocated
  to any selected target.
- Zero incoming candidates means **no inbound provisional binding
  was found**, not zero callers, no impact, or proof of safety.
- External consumers, reflection, dynamic invocation, virtual/interface
  dispatch, DI, generated code, missing project references,
  conditions, and runtime change effects remain unknown.
- This is a snapshot-local, read-only projection; no cross-revision
  symbol identity, test coverage or multi-repository propagation is
  claimed.

## Trust and bounds

The existing graph API performs authentication, exact workspace/repository/
snapshot scope checks, hash verification, joins and fail-closed size/error
handling before the React view receives data. The panel never accesses
the registered live source repository, evaluates imported projects, builds
source, runs analyzers/generators or executes any imported code.

Rendering is bounded to 25 searchable method matches and the first
40 candidate invocations for a selected method. Full evidence remains
available from the authenticated existing graph API; no dropped rows
are silently counted as the complete result.

No new .NET package, JS dependency, SQLite migration, cloud model,
GitHub CI or persistent event is required.

## Acceptance

- On the disposable `One.Main.Run() -> One.Main.Call() ->
  Shared.Contract.Ping()` fixture plus `Two.Direct.Call() -> Ping()`,
  the direct Ping impact view has **two** candidate invocation sites
  with independently observed direct callers `One.Main.Call`
  and `Two.Direct.Call`, not the indirect `Run` method.
- Both candidate call sites preserve exact file SHA-256, source
  line/column, `provisional-local`, and their existing
  shared/disjoint project classifications.
- Unmapped lambda/local-function calls retain file-scoped
  containment; no synthetic caller method is invented.
- A method with no inbound candidate edges shows the explicit
  **no incoming candidates does not establish safety** message.
- Method search and picker enable a user to find existing method
  overloads using source paths and lines; truncated results are
  disclosed.
- Navigation between the callee evidence finder, impact review and
  graph works without changing evidence classifications.
- Existing graph auth, snapshot corruption, DTD, 413 limits, restart
  and original source immutability checks remain green.
- Owner Windows `scripts/check.ps1` GREEN and browser acceptance
  are mandatory before marking the PR ready.

This is the **first one-hop, provisional candidate impact view**,
not the designed full impact investigation engine.
