# Phase 1 Acceptance Checklist

**Current development scope:** PR #1 established the developer shell and architecture baseline. PR #2 added localhost browser pairing and negative security tests. PR #3 adds SQLite workspace **metadata** and an explorer. None is a complete investigative product or a public preview.

## Foundation history and outstanding checks

- [x] Backend compiles with .NET 10 SDK (owner-reported Windows local verification).
- [ ] GET /api/v1/health serves non-sensitive JSON over loopback only.
- [x] React/TypeScript client typechecks and bundles successfully (owner-reported Windows local verification before switching to npm ci).
- [ ] UI connectivity state reflects a real backend request, never fabricated agent progress.
- [ ] Windows PowerShell and POSIX scripts launch and verify trusted application source correctly.
- [x] No repository import, imported-source execution, agent command or plugin loader is exposed (current public surface is health, local auth and workspace metadata).
- [x] Frontend dependency lockfile structurally reviewed and committed (valid JSON, matching direct manifest pins and package integrity fields). 
- [x] Windows local check script GREEN after switching to `npm ci` (owner-reported PR #2 verification).
- [ ] Dependency vulnerability and licensing review completed before preview releases.
- [ ] No GitHub Actions CI required.

The checked build results above reflect owner-reported Windows verification; later changes to the install scripts still require a new run. Other checks remain unchecked until observed.

## PR #2 local-access security checks (owner-reported GREEN)

- [x] Local test builds and runs from scripts/check.ps1 on Windows without errors or warnings (owner reported GREEN).
- [x] Health works over loopback, while a forged Host header is rejected (included in GREEN security suite).
- [x] Unauthenticated session reads receive HTTP 401 (included in GREEN security suite).
- [x] Pairing rejects incorrect codes, disallowed Origins and missing custom request headers (included in GREEN security suite).
- [x] Valid pairing sets a session cookie with HttpOnly and SameSite Strict (included in GREEN security suite).
- [x] Unauthenticated or missing/invalid-CSRF mutations are rejected (included in GREEN security suite).
- [x] Log out revokes the previous session even if the old cookie is replayed (included in GREEN security suite).
- [x] Developer browser flow displays pairing code entry and sign-out; launch scripts show the ephemeral code (owner confirmed pairing works).
- [x] PR #2 did not introduce workspace endpoints; PR #3 limits new mutations to validated, authenticated metadata creation.

Note: tests are deterministic localhost HTTP probes, not a formal security penetration test. Local operating-system account compromise and remote deployment are outside the currently guaranteed threat boundary.

## PR #3 SQLite workspace metadata (pending owner-run verification)

- [ ] Dependency restore and .NET 10 build with pinned Microsoft.Data.Sqlite complete on Windows.
- [ ] React/TypeScript typecheck and bundle build pass.
- [ ] Both security and workspace integration suites pass against disposable databases.
- [ ] Workspace create/list/open requires authenticated browser session; writes enforce CSRF and idempotency.
- [ ] Reusing an idempotency key returns one workspace, with a conflict on changed request content.
- [ ] SQLite preserves workspace records through a clean API stop/start and rejects previous session cookies.
- [ ] Real browser can create a workspace, refresh, restart application, and reopen it from the saved list.
- [ ] Real user local app data remains unaffected by test runs; no imported-repository execution occurs.

Backups, WAL/native packaging and schema migrations beyond v1 remain future validation work. Do not label all of Phase 1 complete after this PR.

## PR #4 local repository registration (pending owner-run verification)

- [ ] Existing PR #3 workspace records survive a schema-v1-to-v2 upgrade without data loss.
- [ ] .NET and React compile, and all security/workspace/registration tests GREEN on Windows.
- [ ] Reject unauthenticated and forged CSRF/Origin requests.
- [ ] Reject invalid, relative, linked, unavailable, root and network paths where applicable.
- [ ] Register at least two distinct folders in one workspace; read and reopen after app restart.
- [ ] Reusing the same idempotency key does not create duplicates; path uniqueness is enforced within the workspace.
- [ ] Same source can be linked to two independent workspaces without sharing record identity.
- [ ] No source files, project/build scripts or Git CLI commands are read, executed or modified.
- [ ] Browser UX clearly labels registrations as **not yet ingested or analyzed**.

Full snapshot ingestion, revision pinning, sandboxing and repository inventory
remain mandatory before source-based conclusions are permitted.

## PR #5 bounded read-only snapshots (pending owner-run verification)

- [ ] .NET API and TypeScript/Vite compile on Windows; all four Node HTTP suites GREEN.
- [ ] Existing saved workspaces and repository registrations survive schema-v2-to-v3 migration.
- [ ] Only paired/CSRF-authorized explicit requests may capture source bytes.
- [ ] Scanner never runs imported project/CLI/agent code and never modifies original source files.
- [ ] Bounded file/size/depth/entry limits fail closed, rather than reporting partial success.
- [ ] Known credential, generated and VCS paths are explicitly excluded in the manifest.
- [ ] Symlink paths are not traversed; source inventory mismatches do not publish a snapshot.
- [ ] Each included path has a hash and a captured immutable-by-app copy with stable snapshot identity.
- [ ] Unchanged source reuses fingerprint/ID; changed source produces a new snapshot while retaining old bytes.
- [ ] Idempotency/replay, auth scoping, saved-manifest/byte tamper detection and restart durability pass.
- [ ] Browser can capture, inspect, restart, and reopen a saved snapshot without running source code.

This is a bounded local source capture, not a race-free sandbox, Git commit
resolver, secret detector, complete analyzer, backup system or public preview.

## PR #8 — parser-backed C# syntax inspection (pending owner-run verification)

- [ ] Roslyn package restore and .NET 10 build pass locally; React typecheck/build GREEN.
- [ ] All existing security, workspace, registration and snapshot tests remain GREEN.
- [ ] Syntax route requires authentication and exact workspace/repository/snapshot IDs.
- [ ] Observations include exact copied-file SHA-256 and one-based line/column anchors.
- [ ] Class/interface/record, method/constructor/property declarations are parsed without running imported code.
- [ ] Changing live source never changes an earlier snapshot's declarations.
- [ ] Syntax errors surface diagnostics and an incomplete status, not a false verified-success claim.
- [ ] Stored manifest or copied-source tampering prevents syntax results (409).
- [ ] Browser syntax inspector shows declarations and limitations; same snapshot works after restart.

Parser-only observations are not a resolved semantic model or proof of project correctness.

## PR #9 — provisional local C# type links (pending owner-run verification)

- [ ] Existing security, workspace, registration and snapshot HTTP suites remain GREEN.
- [ ] .NET 10 build with Roslyn and frontend TypeScript/Vite build pass on Windows.
- [ ] Type-link endpoint rejects unauthenticated requests and wrong workspace/repository/snapshot scoping.
- [ ] Snapshot hash evidence anchors both the source usage and candidate target declaration.
- [ ] A base-type clause and a `new Type()` expression can provisionally bind to a captured source declaration.
- [ ] Unknown/missing/ambiguous types are unresolved, not fabricated as verified.
- [ ] Syntax error count and missing compilation/project context are explicitly disclosed.
- [ ] Manifest/copy tampering denies the entire response; old snapshot is unaffected by new code.
- [ ] Browser displays both provisional matches and unresolved uses with paths, SHA-256 and limitations.
- [ ] Restarting the API preserves the saved snapshot and ability to reconstruct type relationships.

This is not an actual project compilation, resolved call graph, verified inheritance hierarchy or completed graph model.

## PR #10 — static project membership candidates (pending owner-run verification)

- [ ] All prior API/React builds and four localhost HTTP suites GREEN on Windows.
- [ ] Static `.csproj` XML inspection never invokes MSBuild, project targets or imported binaries.
- [ ] Candidate membership retains exact project and file SHA-256 anchors from the same saved snapshot.
- [ ] SDK default patterns, literal Compile Include and literal Compile Remove are distinguished and candidate-labeled.
- [ ] Conditionals, globs, property expansion and import effects remain unresolved, not silently trusted.
- [ ] Anonymous access is denied and cross-workspace/snapshot IDs return 404.
- [ ] Modified saved XML fails 409; malicious DTD input fails 422 without XML entity resolution.
- [ ] Live source changes do not change the saved result; reopening after API restart is consistent.
- [ ] Browser shows project candidates and unresolved rules, never verified compiler membership.

This milestone does not authorize project evaluation, resolved dependency graphs or actual project semantic compilation.

## PR #11 — combined project/link evidence context (pending owner-run verification)

- [ ] Existing .NET 10 build, TypeScript/Vite build, and all localhost HTTP suites remain GREEN.
- [ ] Authenticated endpoint joins type links and project candidates from identical saved snapshot provenance.
- [ ] Shared project-candidate evidence preserves the earlier `provisional-local` Roslyn status.
- [ ] Disjoint candidates are disclosed as uncertain, not invalid or verified project dependencies.
- [ ] Missing project candidates and unresolved target declarations are separate categories.
- [ ] Source and target file SHA-256 plus project hashes remain inspectable.
- [ ] Mis-scoped snapshot IDs return 404; anonymous requests return 401.
- [ ] Saved-byte tampering returns 409; unsafe project DTD returns 422; type-link limit returns 413.
- [ ] Live source edits do not affect saved observations; API restart reconstructs the same results.
- [ ] Browser `Inspect link context` displays all four classifications and reasons.

No project evaluation, resolved dependency graph, method-call graph or automatic confidence promotion is implemented.

## PR #12 — navigable C# snapshot evidence graph (pending owner-run verification)

- [ ] Existing .NET 10 API, TypeScript/Vite build and localhost HTTP suites GREEN on Windows.
- [ ] Graph endpoint is authenticated, correctly scope-restricted and tied to a saved snapshot fingerprint.
- [ ] Graph includes project, source, observed candidate declaration and unresolved-occurrence nodes.
- [ ] Every directed edge joins existing nodes and preserves a captured project or source SHA-256.
- [ ] Compile membership remains candidate-only and type-use edges remain provisional-local or unresolved.
- [ ] No unresolved type spelling becomes a fabricated target declaration.
- [ ] Node/edge IDs, order and provenance are deterministic across repeated calls and restart.
- [ ] Live tree changes cannot modify saved graph results; saved-byte/manifest tampering returns 409.
- [ ] Unsafe project DTD returns 422 and upstream file/byte/usage limits fail 413 with no partial graph.
- [ ] Browser graph search, node selection, bounded neighborhood view and one-hop navigation work.
- [ ] Browser labels graph limitations and never claims verified compiler dependencies.

This is an in-memory on-demand evidence graph, **not** durable typed graph persistence,
real solution compilation, call graph or proof of architectural behavior.

## PR #13 — syntax-observed C# declarations in graph (pending owner verification)

- [ ] Existing .NET 10 API, TypeScript/Vite build and localhost HTTP suites GREEN.
- [ ] Graph combines syntax report with candidate project/link observations from the exact saved snapshot.
- [ ] Graph reports syntax analyzer version, syntax declaration count and updated graph analyzer version.
- [ ] Unreferenced classes, nested classes, method overloads, and properties appear as source-located nodes.
- [ ] Syntax-only declarations link to saved source files with exact captured SHA-256 and source line/column.
- [ ] Referenced target declarations reuse exact file/hash/span graph identity and containment edge.
- [ ] Overload source spans preserve separate identities despite identical display names.
- [ ] The graph remains deterministic after repeated requests, live tree edits and API restart.
- [ ] Existing authentication, snapshot 404, tamper 409, unsafe XML 422 and bounds 413 remain functional.
- [ ] Graph browser exposes **Parsed declarations (unreferenced)** and source syntax count; confidence labels remain honest.

No resolved compiler symbols, project evaluation, call graph, durable graph persistence or source execution is introduced.

## PR #14 — observed C# invocation sites (pending owner verification)

- [ ] Windows .NET 10 build, TypeScript/Vite build, and localhost HTTP integration suites GREEN.
- [ ] Invocation observations use only SHA-256-verified, saved C# text, no project loading or source execution.
- [ ] Graph v3 reports invocation inspector version, total invocation sites and directly method-contained sites.
- [ ] Syntax invocation nodes and `observed-syntax` containment edges preserve exact source path/hash/line/column.
- [ ] Two same-named method overloads each own their direct invocation occurrences with distinct source-span IDs.
- [ ] Nested lambda and local-function invocations remain file-scoped, not incorrectly assigned to outer methods.
- [ ] Written method names never create fabricated callee or verified call/dependency edges.
- [ ] 4,501 invocation-site fixture fails HTTP 413 while saved manifest stays accessible.
- [ ] Existing auth/scope, snapshot tamper 409, unsafe project DTD 422, restart stability and previous graph claims pass.
- [ ] Graph browser supports the **Invocation sites (callee unresolved)** filter and one-hop navigation from caller/file to occurrence.

This slice creates a **call-site syntax inventory**, not a compiler-bound
method-to-method call graph or execution proof.

## PR #15 — provisional source-local callee links (pending owner verification)

- [ ] Windows .NET 10, TypeScript/Vite, and localhost HTTP integration suites GREEN.
- [ ] Synthetic Roslyn callee analysis is bounded, reference-free, in-memory, and reads only hash-verified snapshot copies.
- [ ] Graph v4 reports binder version, provisional candidate count and unresolved count; counts partition all observed invocation sites.
- [ ] One ordinary method with a unique captured declaration may create a `candidate-callee` / `provisional-local` edge from its invocation-site node to its exact method syntax node.
- [ ] Every candidate edge preserves the invocation's captured SHA-256, path, one-based line and column.
- [ ] A missing/ambiguous/nonordinary/external callee remains unresolved with no guessed declaration or outgoing edge.
- [ ] Independent invocation syntax and binding reports join bijectively on exact path/hash/span; mismatch fails 409 without partial results.
- [ ] Existing direct caller containment, overload, lambda and local-function protections remain intact.
- [ ] Existing auth/scope, tamper 409, unsafe XML 422, per-inspector 413 bounds and restart stability still pass.
- [ ] Browser displays candidate/unresolved counts, **Invocation sites (candidate or unresolved)** filter and evidence-backed provisional callee navigation.

This is **not** a verified method-to-method call graph, execution proof, project loading, or impact model.

## PR #16 — callee static project-candidate context (pending owner verification)

- [ ] Windows .NET 10, TypeScript/Vite and local HTTP integration suites GREEN.
- [ ] Graph v5 retains all provisional method-callee edges with unmodified `provisional-local` status.
- [ ] Project-candidate context of each callee edge is exactly one of shared, disjoint or unknown; counts partition provisional callee count.
- [ ] Shared project candidates use identical saved project path and SHA-256 on both sides.
- [ ] Source, target and shared project candidates list saved .csproj path/hash, inclusion basis and unresolved-rule count.
- [ ] Disjoint candidates never imply a call is impossible; missing candidates never imply exclusion.
- [ ] Unresolved callees have no candidate edge or fabricated project-context evidence.
- [ ] Existing snapshot provenance, authorization/scope, tamper 409, XML DTD 422, bounds 413 and restart stability remain intact.
- [ ] Browser displays project-context counts and exact candidate evidence on selected callee links.
- [ ] Browser provides **Find callee project evidence** with direct candidate links, all/shared/disjoint/unknown filtering, expandable project evidence and a useful explicit empty state when a snapshot has zero candidates.
- [ ] Owner browser acceptance confirms honest confidence labels and existing graph navigation.

This enriches existing synthetic call edges with **unevaluated static
project evidence**; it does not load projects, resolve real compiler
symbols or certify runtime dependencies.

## PR #17 — first one-hop C# change-impact explorer (pending owner verification)

- [ ] Windows .NET 10 API, React/TypeScript/Vite build and local HTTP/security integration suites GREEN.
- [ ] **Explore direct change-impact candidates** panel is discoverable in the existing graph explorer and starts with a candidate-backed method when one exists.
- [ ] Method picker supports substring search by name or saved path, displays distinct source-line identities for overloads and warns when its 25-entry display limit is reached.
- [ ] A selected method shows only direct incoming `candidate-callee` / `provisional-local` invocation sites to that exact saved declaration ID.
- [ ] Every candidate result preserves the invocation's observed source location/SHA-256 and `observed-syntax` method/constructor or file containment.
- [ ] An indirect `Run -> Call -> Ping` chain does not falsely include `Run` as a direct caller of `Ping`.
- [ ] Project context, source/target/shared candidate hashes and unresolved-rule warnings remain visible without confidence promotion.
- [ ] Zero inbound candidates displays **not proof of safety**; unresolved invocation sites are reported as unallocated, not silently treated as no impact.
- [ ] UI navigation from candidate finder to the target method's one-hop review and from results into the full graph works.
- [ ] Existing exact snapshot authorization, tamper 409, unsafe XML 422, size 413 and restart/read-only source invariants remain GREEN.
- [ ] Owner browser acceptance confirms honest evidence classifications before PR is made ready.

This is an **on-demand direct candidate review checklist**. No
new persistence, analyzer, real call graph, transitive inference or
tested breakage prediction is part of PR #17.

## Next foundation gates

1. Continue local security hardening and adversarial browser tests as capabilities expand.
2. Extend SQLite from workspace **metadata** to durable event/evidence state, versioned migrations and consistent backups.
3. Bundled React assets with a self-contained .NET launcher and setup wizard.
4. Clean-machine validation on Windows x64, Linux x64, macOS ARM64 and x64.
5. Safe snapshot ingestion with malicious repository/project fixture tests; degraded semantic analysis must be explicit.

The product's Phase 1 is not complete when the first PR merges. Preserve all v0.1 and v0.2 acceptance gates. 
