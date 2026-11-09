# ADR 0007 — Provisional snapshot-local C# type links

- **Date:** 2026-11-09
- **Status:** Draft implementation; owner-run Windows verification pending
- **Builds on:** ADR 0004 (verified snapshots), 0005 (inventory), 0006 (Roslyn syntax)
- **Architecture:** Code Archeologist v0.2, §8 polyglot analysis contracts and §9 evidence-qualified relationships

## Decision

Add the first **source-local, provisional C# type relationships** without
loading imported project files. This is a deliberately narrow stepping stone
between plain syntax parsing and real project-aware semantic analysis.

The endpoint is read-only and scoped to one previously captured snapshot:

`GET /api/v1/workspaces/{w}/repositories/{r}/snapshots/{s}/csharp-type-links`

This route is session protected and requires exact workspace/repository/snapshot
identity. It first verifies the saved manifest and **every included file's**
SHA-256. Then it reopens and verifies each included `.cs` artifact from the
application-managed copy. The registered live repository is never reopened
for type-link analysis.

All C# syntax trees from the selected snapshot are placed in one temporary,
reference-free **Roslyn synthetic compilation**. There is **no project
evaluation**, NuGet restore, external `MetadataReference`, MSBuildWorkspace,
emit, source generator, plugin, imported compiler analyzer, script execution
or runtime code execution.

The initial relationship types are:

- `base-type`: the type written in a class/interface/record base-type list.
- `object-creation`: the type explicitly written in `new SomeType(...)`.

Other references are not examined. Target-typed `new()`, dependency injection,
method call graphs, symbol imports, attributes, generic type arguments, and
project/package references are future work.

## Evidence model and conservative status

Each observed usage includes:
- source snapshot ID and snapshot fingerprint;
- exact captured source path, file SHA-256, 1-based line/column, and node span;
- the spelling of the type at the usage site;
- usage kind, link status and explanatory reason.

When Roslyn binds a type to **exactly one** captured source declaration, the
report includes the candidate target's file path, SHA-256, namespace, name,
line/column and span. The status is **`provisional-local`**, *not* verified.
There is deliberately no `verified` status in this milestone.

If the name is missing, cannot be bound, belongs to a reference not in the
captured files, or maps to multiple partial declarations, the status is
`unresolved` with a null target and explanation. The report explicitly counts
provisional and unresolved uses; it exposes syntax error counts.

### Why even a Roslyn binding is provisional

A real solution can contain multiple independent projects, conditional
compilation, project reference rules, aliases, generated files, and metadata
dependencies. This first pass synthetically places all captured `.cs` files
into **one compilation without reference assemblies**. A link could therefore
connect files that never compile together in reality. The link is an
explainable *hypothesis grounded in code spans*, not a proved dependency.

No user-facing report may promote these links to resolved project edges,
compiled symbols, runtime calls, inheritance validity or change-impact proof.

## Bounds and failure behavior

- Up to **250 `.cs` files** and **8 MiB** total captured C# bytes per request;
- Up to **6,000** explicit type-use observations;
- Strict UTF-8 input, no replacement-character fallbacks;
- Exceeding limits returns HTTP 413 rather than partial successful results;
- Corrupt saved manifest or copied bytes return conflict rather than links;
- Mis-scoped snapshot ID returns 404 and unauthenticated access returns 401.

The existing filesystem-race and secret-at-rest caveats of ADR 0004 remain.
The synthetic compilation is not a security sandbox or evidence of a successful
real build. This analysis uses memory/CPU and is intended for owner-controlled
local repositories, not adversarial input with a strong isolation requirement.

## Acceptance test expectations

- Existing four HTTP suites and .NET/React builds remain GREEN.
- A pair of independently captured C# files defining and referring to a class
  produces provisional `base-type` and `object-creation` links to the
  captured declaration's exact path/hash/line anchor.
- An unknown type remains unresolved with no fabricated target.
- Authentication and exact snapshot scoping are enforced.
- Tampering with a saved source file or its manifest prevents any links.
- Old snapshots remain unchanged after new C# source revisions.
- Restarting the app preserves the ability to reconstruct observations
  from the previously captured source snapshot.
- Browser displays both provisional and unresolved evidence **with
  limitations**, not as a verified dependency graph.

Project-aware symbol binding, real `.csproj` evaluation under a restricted
non-executing loader, and typed SQLite graph relationships remain separate
future milestones.
