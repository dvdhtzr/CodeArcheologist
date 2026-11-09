# ADR 0006 — Bounded Roslyn syntax-only C# observations

- **Date:** 2026-11-09
- **Status:** Draft implementation; owner-run Windows verification pending
- **Prerequisites:** ADR 0004 saved immutable-by-app source snapshots; ADR 0005 filename-only manifest inventory
- **Design reference:** approved Code Archeologist v0.2 C# structural foundation and evidence rules

## Scope

The first parser-backed C# capability identifies **syntactically present**
namespaces, classes, structs, interfaces, records, enums, delegates, methods,
constructors and properties. Each observation includes the exact workspace,
repository and snapshot IDs; snapshot fingerprint; artifact relative path
and SHA-256; node kind/name; textual containing namespace and parent type;
one-based start/end line and column; source span offset/length; and optional
parameter count.

This is intentionally **syntax analysis, not semantic compilation**.
Neither a namespace/type/member name nor a declaration's presence proves
its resolved identity, reachability, API use, dependency, compilation success,
runtime behavior or code ownership. Partial types and overloads may have
identical names, and method signatures can differ beyond parameter count.

## Non-executing processing path

1. The paired browser calls an authenticated, read-only, scoped endpoint:
   `GET /api/v1/workspaces/{w}/repositories/{r}/snapshots/{s}/csharp-syntax`.
2. Resolve the snapshot by exact workspace + repository + snapshot ID.
   A mismatched ID combination returns 404.
3. Verify the manifest digest **and all saved bytes**, as the earlier snapshot
   service already requires. Read each eligible `.cs` file **again** from the
   application-managed captured copy and verify the original file hash before
   parsing. The original registered repository folder is never opened by
   this route.
4. Parse UTF-8 saved `.cs` files with pinned
   `Microsoft.CodeAnalysis.CSharp` 5.9.0, using
   `CSharpSyntaxTree.ParseText` and `LanguageVersion.Preview` with no custom
   preprocessor symbols. Walk syntax nodes only; no `MSBuildWorkspace`,
   compilation, metadata references, source generators, analyzers, restore,
   project evaluation, scripts, build targets, process spawning or Git.
5. Return deterministic declaration order (file path then syntax traversal),
   parser diagnostics and explicit limitations. A recoverable syntax error
   yields `hasSyntaxErrors: true` and syntax-error diagnostics; such results
   must not be displayed as fully complete.
6. Reject the entire request with 413 if the snapshot exceeds 1,000 `.cs`
   files or 32 MiB total C# source, or produces over 25,000 supported
   declarations or 1,000 diagnostics. Invalid UTF-8 yields 422 instead of
   substituting replacement characters. These are deliberate first-slice
   ceilings, not claimed scalability targets. No partial successful report
   is returned after hitting a limit.
7. The result is computed on demand from captured bytes and is rebuildable;
   it does not create a new database version or durable graph/evidence row.

## Evidence classifications

- **Directly observed:** the exact code bytes, copied relative file path,
  content digest, and parser span within that snapshot.
- **Syntactically observed:** declarations Roslyn can recognize under the
  stated language/preprocessor configuration.
- **Unresolved:** symbol binding, aliases, preprocessor-disabled code, partial
  declarations across files, imports, dependencies, inheritance identities,
  call graphs, API contracts, generated code, behavior, type checking and
  actual project compilation.

Only the first two categories appear in the response as observations.
Everything else remains explicitly unknown, regardless of how confidently
an LLM or documentation might describe it.

Snapshot provenance does not mean the original source was a trustworthy
Git commit. The source revision identifier remains the recorded filesystem
content fingerprint, not an unverified branch name.

## Security and privacy

This endpoint accepts no user-provided source path or C# text; it accepts
only opaque, scoped snapshot IDs. It does not execute untrusted source, even
if that source contains MSBuild targets or instructions inside comments.
It requires the same local browser session controls as the other read routes.

The existing snapshot storage is not an OS-enforced race-free sandbox or
encrypted vault. File paths and diagnostic messages can include sensitive
metadata. The syntax response stays on the user's loopback app.

## Verification

Extend disposable end-to-end snapshot tests to check:
- Unauthenticated denial and cross-scope 404;
- Correct namespace/type/member kinds, names, line numbers, file hashes,
  and nested type context;
- Source change creates a new syntax result while the old snapshot remains;
- Corrupted stored manifest or file yields conflict, not synthetic observations;
- Invalid syntax surfaces diagnostics and incomplete status, without executing;
- Persistence of the same snapshot's syntax after restarting the local API.

This first parser is not a semantic Roslyn workspace, code graph, retrieval
index, MAF agent, deep compiler evaluation or complete phase 2.
