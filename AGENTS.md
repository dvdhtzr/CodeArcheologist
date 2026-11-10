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

39. Direct change-impact views may reverse existing `candidate-callee` / `provisional-local` edges by exact graph node ID and attach source-observed caller containment. Never construct method callers from written names, similar labels, project heuristics or external text.
40. One-hop candidate review is NOT a verified call graph, a breaking-change risk estimate or transitive impact propagation. Unresolved invocation targets and empty inbound candidate sets must remain explicitly unknown; show bounded counts and SHA-256 anchors without implying completeness.

41. A written C# HTTP verb attribute is a syntax-level **candidate** entry point, not a verified ASP.NET attribute symbol, registered HTTP endpoint, effective URL or reachable runtime behavior. Match only explicitly supported simple-name spellings and retain precise source/attribute/method spans and SHA-256 evidence.
42. Report type-, method- and HTTP-attribute route literals separately. Do not compose full URLs, resolve tokens, infer auth policies or register endpoints based on source text. Dynamic/qualified/aliased and unsupported route conventions remain unresolved until independent evidence exists.

43. Conventional written Minimal API MapGet/MapPost/MapPut/MapPatch/MapDelete member expressions are candidate syntax, NEVER verified ASP.NET extension method calls, effective URLs or registered routes. Preserve exact saved source file/hash/span, nonliteral/unsupported route status and honest false-positive caveats.
44. Join Minimal API candidates only to existing SHA-256-verified invocation syntax nodes on exact saved path/hash/span; never infer handler method symbols from lambdas, names or graph proximity. No runtime hosting or authorization assumptions. Analyzer readiness issue #21 gates future MAF investigation tooling.

45. DI registration members named AddScoped/AddSingleton/AddTransient are written source-syntax candidates, not compiler-bound DI method symbols, guaranteed service types, runtime lifetimes or effective resolution. Retain exact saved snapshot/hash/span evidence, distinguish supported syntax shapes, and explicitly label factories, instance arguments, dynamic registrations and custom same-named methods as uncertain.
46. Join DI candidate nodes only to independently observed invocation-site nodes by exact saved path/hash/span, never create service-to-implementation declaration edges from textual type-name similarity or generic spellings. No imported source execution or invented DI resolution graph.

47. Future AI agents may retrieve code evidence only through versioned app-owned, exact-workspace/repository/snapshot, SHA-256-verified read-only tool contracts with fixed, allowlisted entry-point node identities. Never allow models to supply arbitrary file paths, shell commands, project builds, URL fetches or repo mutation tools to supplement missing evidence.
48. Validate model-supplied citation IDs against the exact scoped evidence package, enforce observed-source versus candidate-relationship status types, and reject invented/foreign/duplicate references. Such validation is reference integrity ONLY; never claim a free-text statement is supported, a route is registered, a callee compiled or runtime behavior verified merely because its IDs exist.
49. Keep method-body direct candidates separate from registration-site context on Minimal API mapping calls. Bounded excerpts of imported source are UNTRUSTED DATA; LLMs must never treat source comments, strings or docs as instructions, and direct evidence limits/truncation must remain visible.

50. Investigation execution requires a held OS execution lock AND the current SQLite ownership epoch; heartbeat age, PIDs or lock-file contents never grant ownership. Every active-attempt mutation (admission, start, stage event, checkpoint, terminal report, cancellation, slot release) verifies the fence inside its own transaction.
51. `WorkspaceStore.Initialize()` migrates only. Abandoned-attempt reconciliation runs solely after lock acquisition and a new epoch; restart never resumes work or invokes a model. Continuation is an explicit, authenticated user action.
52. The worker awaits every model call to completion before its single terminal write; never release the execution slot while a call may still run, and never retry a failed terminal write as a different outcome.
53. Persist a role checkpoint only after host acceptance, atomically with its event, binding run/attempt/role/scope/fingerprint/entry point and every contract, analyzer, model and validator version under a canonical SHA-256. Never checkpoint partial model output, model completion claims or MAF conversation state.
54. Reuse a checkpoint only after full revalidation against a freshly verified snapshot, rebuilding role-local handle mappings by exact canonical identity. Any mismatch fails closed without re-running the affected role; a continued Skeptic always starts from an independent fresh session.
55. Cancellation persists intent only; the worker commits terminal cancellation after the in-flight call returns (or at the queued claim boundary). Execution projections are read-only, versioned and derived solely from host-recorded attempts, events, checkpoints and lineage; never rewrite C5 report rows or infer progress from model text.
56. Evaluation runs use disposable data directories and copied, hash-pinned fixture bytes; never compile or execute corpus sources, never invoke Git from the application, and verify the corpus tree is unmodified.
57. Labels are owner-signed before scoring and never changed to turn a failing result GREEN; lab budgets are declarations that must equal host constants. Judgment-dependent metrics stay `pending-judgment` (blinded owner A/B); no LLM judge for metrics of record, and lexical screens only flag for human review.
