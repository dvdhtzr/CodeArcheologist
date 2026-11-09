# Engineering Rules

The ratified specification is in docs/architecture, with owner approval recorded in docs/decisions/0001-architecture-approval.md. Original design documents are preserved unchanged and contain historical pending-approval language.

1. Never execute code, build targets, restore scripts, or arbitrary tools from *imported* repositories in the default analyzer path. Compiling this trusted application for local verification is separate.
2. Treat source, model output, plugins, and Markdown as untrusted input; enforce authorization and validate typed evidence independently.
3. No fabricated citations, verified classifications, model self-scored confidence, completion, or fictional progress.
4. The operational evidence ledger and source snapshots are authoritative for observed code; preserve accepted human vault knowledge and reconcile conflicts.
5. Every investigative task must trace to an evidence gap, parent question or failed gate; model orchestration does not own state or authorization.
6. Never silently overwrite human-authored notes or user repositories.
7. Loopback is not authentication. Until authenticated local session and Origin/Host/CSRF controls exist, expose no mutable or sensitive endpoints.
8. Third-party plugin opt-in does not imply isolation. Do not run untrusted executable plugins without an enforceable boundary and truthful privilege disclosure.
9. Develop through focused PRs. Run checks locally and accurately report observed results; GitHub Actions CI is intentionally disabled.
10. No cloud credentials, mandatory Docker, paid APIs or database servers for the local-first baseline.

11. Any integration test involving application persistence MUST use a disposable, isolated data directory, never real user workspaces; verify restart/recovery using the same isolated directory.
12. Workspace create endpoints require authenticated user, CSRF and a request-scoped idempotency key. Future update/delete mutations require optimistic concurrency and negative tests.

13. Registering a local source is metadata-only. Never read project files, invoke Git, restore packages, execute imported build targets or modify original source trees in this flow.
14. Revalidate every directory entry and symlink when a future immutable snapshot is acquired; a successful registration check is not a trusted snapshot or proof of revision identity.

15. Snapshot creation requires explicit paired browser consent per request, never implicit registration/background watching; captured bytes are untrusted and must never cause project/tool/script execution.
16. Snapshot content must be recorded by immutable-by-app identity with exact relative paths, file hashes, exclusion list and capture policy version. Never equate a filesystem fingerprint with a verified Git commit.
17. No silent partial capture: limits, unauthorized paths, content changes and missing source must fail closed, leaving no trusted record. Integration tests must verify source non-modification and saved-artifact integrity.
18. Do not claim that revalidating paths or checking symlinks before file opens is an OS-enforced race-free sandbox; any elevated threat model requires restricted-handle acquisition and adversarial filesystem race tests.

19. Filename-only inventory labels (language and file role) are heuristic, even when the underlying snapshot path/hash is verified. Never promote them into verified code semantics, dependencies, API routes or behavior.
20. Inventory must be scoped to the exact workspace/repository/snapshot, derive from a verified immutable snapshot, remain rebuildable, and fail closed on manifest/byte tampering.

21. Roslyn syntax observations must be extracted from hash-verified captured files without creating an MSBuild workspace, running project evaluation, loading source generators or executing third-party analyzers.
22. Namespace/type/member declarations and their spans are syntactic observations only. Do not claim binding, inheritance, dependencies, call graphs, project compilation or behavior without separately verified evidence.

23. Roslyn's reference-free synthetic compilation is allowed solely for provisional source-local type links; never emit it or execute imported source code, build targets, plugins, external analyzers or generators.
24. Synthetic binding across captured files is *not* project-verified. Use status `provisional-local` or `unresolved` and never silently promote to a verified inheritance, dependency or impact edge.

25. Project membership obtained from static `.csproj` XML and SDK patterns is candidate-only. Do not evaluate imported MSBuild targets or claim the candidate list is effective compiler input.
26. Reject XML DTD/external entities and report conditional, wildcard, imported or property-expanded Compile rules as unresolved; do not silently infer their effect.

27. Joined static project candidates and synthetic Roslyn type links remain hypotheses; never promote shared candidates to verified compiler inputs or real dependencies, and never interpret disjoint candidates as proof of invalid binding.
28. Joined relationship evidence must preserve upstream source/target SHA-256, project hashes, snapshot IDs and unresolved statuses and propagate all upstream integrity/limit failures without publishing partial results.

29. Graph nodes/edges must be deterministic, snapshot-scoped, backed by copied-file SHA-256 and source spans when available, and referentially consistent. Never create an imagined target for an unresolved reference.
30. A graph visualization does not upgrade candidate compile-item membership, synthetic type links or project context into verified dependencies or impact propagation; graph size limits must fail closed.

31. Syntax declaration graph nodes represent captured Roslyn parse locations; these are not compiler-verified symbols or runtime behavior. Reuse exact file/hash/span target identities and their containment edges instead of duplicating nodes.
32. Do not identify overloads solely by name; preserve distinct source spans. An expanded syntax graph must not claim a method-call, resolved project dependency, or complete symbol inventory.

33. Invocation sites are captured syntax, NOT compiler-bound methods or runtime calls. Method/constructor containment is allowed only when that declaration is the nearest supported execution scope; never assign lambda/local-function/accessor invocations to an enclosing method by convenience.
34. Do not add a written-name-based callee edge to the evidence graph. Every invocation site must have exact saved file/hash/span evidence and honor bounded fail-closed inspection limits.

35. Callee edges from reference-free synthetic Roslyn models must be explicitly `provisional-local` and target an existing, SHA-256-verified saved source method declaration. Never promote them to real compiler, project, runtime or impact edges.
36. Never resolve an invocation by matching names, argument counts or candidate symbols when unique Roslyn source binding fails. Join independent invocation observations by exact source file/hash/span; inconsistent or missing evidence must fail closed rather than silently drop edges.

37. Qualifying a provisional method callee with source/target .csproj membership **never** upgrades its original `provisional-local` status. Shared/disjoint/unknown static project candidates are evidence, not verified compiler membership, real project references or runtime behavior.
38. When enriching call edges, reuse the existing hash-verified project inspection report and preserve exact .csproj SHA-256, inclusion basis and unresolved-rule count; never fabricate project candidates for unresolved callees.
