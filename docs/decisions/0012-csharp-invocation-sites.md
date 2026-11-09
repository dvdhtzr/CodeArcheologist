# ADR 0012 — Observe invocation sites without claiming callee binding

- **Date:** 2026-11-09
- **Status:** Draft; owner-run Windows and browser acceptance pending
- **Prerequisites:** ADR 0006 (safe syntax parsing), ADR 0010 (bounded graph), ADR 0011 (source declarations)
- **Architecture:** Approved Code Archeologist v0.2 — deep C# with explicit evidence provenance, non-executing imported-source analysis

## Problem

The evidence graph shows file and method declarations, but not written
invocation expressions such as `Load()` or `service.Fetch(id)`.
A useful first step toward method-level analysis is to show which *observed
method body contains an invocation expression*. Source syntax alone cannot
establish which method is invoked: overload resolution, extension methods,
virtual/interface dispatch, delegate invocations and dynamic behavior
all require substantially more context.

## Decision

Create `CSharpInvocationSyntaxInspector` using Roslyn **syntax-only**
`CSharpSyntaxTree.ParseText` over previously captured SHA-256-verified
`.cs` files, without MSBuild, `CSharpCompilation`, package restore,
project loading, imported analyzers or code execution.

Only `InvocationExpressionSyntax` is inspected in v1. For each
observation, retain:
- The saved file path and full SHA-256.
- One-based source line and column, exact syntax span start and length.
- The written invoked expression (display-limited to 160 characters)
  and count of syntactic arguments.
- The directly containing method/constructor's name, kind and exact span,
  **only if** it is the nearest relevant execution scope.

Nested lambdas, local functions, accessors, field/property initializers,
operators and top-level statements **must not** be misattributed to an
enclosing method. Their invocation sites are associated with the saved
file only. This is intentionally incomplete but avoids false claims.

Graph v3 (`csharp-snapshot-evidence-graph-v3`) adds:
- `invocation-site` nodes identified by saved path, SHA-256 and invocation
  syntax span.
- A directed `contains-invocation` edge from the directly enclosing
  method/constructor declaration to the site, or from its C# file when
  the method owner is not supported.
- Status `observed-syntax`; each edge cites saved file/hash/line/column.
- `invocationAnalyzerVersion`, `invocationCount` and
  `methodContainedInvocationCount` summary fields.

**No invocation node has an outgoing callee/dependency edge.**
The written identifier `Load` is not matched to a method named `Load`,
including when there is exactly one same-named source declaration.
The occurrence is not categorized as a verified runtime call, and no
project-context candidate relationship is fabricated from its spelling.

The browser adds an **Invocation sites (callee unresolved)** node filter
and shows invocation-site counts. A selected method displays contained
invocation nodes as direct neighbors, and a selected invocation shows
the owning method or source-file evidence.

## Provenance, limits and failure

All analyzers must agree on workspace, repository, snapshot and manifest
fingerprint before the graph can be returned. The new inspector uses
the same verified saved-file reader; a hash mismatch rejects the graph
with HTTP 409. Invalid UTF-8 returns 422.

The new inspector limits 250 C# files, 8 MiB source text and 4,500
invocation sites per snapshot. It returns HTTP 413 on an exceeded
limit (not partial success). The graph's existing 10,000-node /
16,000-edge caps still apply. Existing authorization, exact scope
checks and unsafe project-XML handling remain unchanged.

No imported code or build steps run, no network calls are made, no database
migration occurs, and no graph is persisted independently of the source
snapshot. The analysis does not write to the registered repository.

## Known limitations

- This identifies **invocation syntax** only, not object creation,
  delegate target identity, operator invocations, property access,
  implicit calls, reflection or dynamic resolution.
- A written invocation may name a local function, delegate or unknown
  external method; there is no callee identification yet.
- Lambda/local-function invocations are file-scoped in this slice;
  no false outer-method association is preferable to speculation.
- `LanguageVersion.Preview` with default preprocessor symbols may differ
  from the actual project's language and active compilation branches.
- Syntax errors may yield recovered/incomplete nodes. No confidence score
  is implied by parse success.
- Overload method identities are exact source spans, not compiler symbols.
  IDs remain stable within a saved snapshot, not across revisions.
- This is not a method-to-method call graph or an impact model.

## Acceptance

Owner-run `scripts/check.ps1` must pass, including new fixture assertions:
two same-named overloads each contain their direct invocation, lambda and
local-function invocation sites remain associated with the file, each site
retains the exact SHA-256 and line/column, and no callee edge is invented.
A 4,501-site fixture must return 413 while its saved manifest remains
readable. Existing graph integrity and restart/tamper/DTD checks must pass.

Browser verification must show the invocation filter, select a method,
navigate to written call sites, and display the `observed-syntax`
classification and unbound-callee limitation. Only then mark the PR ready.
