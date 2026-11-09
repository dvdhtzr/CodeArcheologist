# Code Archeologist

**Status: Early local-first development. Saved snapshots, source inventory, Roslyn syntax, provisional C# type links, candidate project membership, link context and a declaration/invocation-aware evidence graph explorer are available. Synthetic callee candidates now include static shared/disjoint/unknown project-membership context. All callee edges remain provisional; project-verified call targets, dependency resolution and agent intelligence are not implemented.**

Code Archeologist aims to be a local-first, cross-platform software intelligence platform that analyzes one or more source repositories, constructs evidence-backed architecture and dependency models, supports adaptive multi-agent investigation, and preserves institutionally reviewed knowledge in an Obsidian-compatible vault.

The approved architectural baseline consists of [v0.1](docs/architecture/Code-Archeologist-v0.1.md), the [v0.2 technical design](docs/architecture/Code-Archeologist-v0.2.md), and the [subsequent owner-approval record](docs/decisions/0001-architecture-approval.md). Historical status wording in the original documents is preserved intentionally.

## Current developer milestones

- ASP.NET Core .NET 10 API bound only to loopback, exposing a public health endpoint and a protected local browser session.
- React + TypeScript development client with real API health, local browser pairing and a persistent workspace explorer.
- SQLite-backed workspace creation, listing and reopening across app restarts, protected by local pairing and CSRF checks.
- Authenticated registration of multiple existing local Git working trees or ordinary directories per workspace. Registration saves path metadata and does **not** read, build or execute source content.
- Separately authorized **Capture read-only snapshot** operation: includes eligible file bytes in application-managed storage, excludes known sensitive/vendor/binary paths, hashes included files, preserves prior revisions, and exposes an inspectable manifest. Capture never executes imported code or invokes Git/project builds.
- **Inspect source inventory** on a saved snapshot: a deterministic, read-only filename/extension classification of probable languages and file roles with exact manifest paths, SHA-256 anchors and exclusions. This is **not** a parser, graph, dependency finder, agent-based analysis or proof of functionality.
- **Inspect C# syntax** on a saved snapshot: a Roslyn parser finds namespace, type and member declarations and returns source spans, SHA-256 anchors and diagnostics. It parses copied, verified bytes only: no builds, semantic compilation, project loading, source generators or execution. This is not a call graph or symbol-resolution engine.
- **Inspect C# type links** on a saved snapshot: uses a bounded, reference-free Roslyn synthetic compilation to propose links between base-type clauses / explicit object creations and their source declarations. Every match is `provisional-local`, not a verified project dependency. Missing/ambiguous targets remain unresolved; no project evaluation or execution.
- **Inspect C# projects** on a saved snapshot: safely parse hash-verified `.csproj` XML and show candidate file membership from known SDK default patterns and literal unconditional Compile includes/removes. Unknown or conditional rules remain unresolved. This does **not** invoke MSBuild or prove which files actually compile.
- **Inspect link context** on a saved snapshot: cross-checks provisional Roslyn type references against candidate project-file membership and labels each relation shared, disjoint, unknown or unresolved. It displays file and project SHA-256 evidence, without upgrading candidate matches to verified compiler relationships.
- **Explore C# evidence graph** on a saved snapshot: navigable project/file/declaration/type-use/**invocation-site** nodes connected by typed, SHA-256-anchored evidence edges. In addition to observed method/constructor containment, an invocation may have a **provisional-local candidate-callee** link to a uniquely bound captured method under a reference-free synthetic Roslyn model. Missing/ambiguous targets remain unresolved, without guessed edges. Search, filters, neighborhood overview and one-hop traversal are available. This in-memory projection is **not** a verified project call graph or a persistent graph database.
- Local Windows PowerShell and Unix shell startup/build scripts with disposable security and workspace persistence tests.
- Safety, contribution, acceptance and architecture documentation.

**Not yet implemented:** immutable Git commit resolution, full safe hostile-source ingestion, complete workspaces (vault mapping, backup/restore), production-grade hosted authentication, agents/MAF, quality engine, plugins, source sandbox, graph, vector retrieval, vault reconciliation, production packaging, or complete Mission Control. Snapshot capture is available only by explicit user action and is not suitable for highly sensitive or adversarial repositories without further hardening.

## Development

Requires a .NET 10 SDK and Node.js 22.12+ with npm **for working on the source**. Packaged releases are intended to eliminate those end-user prerequisites.

Windows: run scripts/check.ps1 then scripts/start.ps1.
macOS/Linux: run bash scripts/check.sh then bash scripts/start.sh.

**Local pairing:** the start script prints a new 32-character pairing code. Enter it in the browser to unlock the current process-scoped local session. The API uses an HttpOnly SameSite=Strict cookie, a server-side revocation list and a CSRF header for state-changing requests. Sessions expire after 30 minutes and are invalid after restarting the API. The code is shown only in local terminal output and must not be shared. The pairing code can be retried until the API stops, with temporary lockouts after repeated failures. Pairing is for localhost-only development, **not remote or multi-user access**.
Open http://127.0.0.1:5174/ ; the API is on http://127.0.0.1:5127/ and supports GET /api/v1/health.

The committed `package-lock.json` pins the frontend dependency graph; local verification uses `npm ci` to reproduce it. Previously successful Windows developer verification does not establish cross-platform packaging. GitHub CI is intentionally absent. The check scripts also run security and workspace integration tests against the trusted API with isolated, temporary SQLite databases. Stop the development server before running the check script because both use API port 5127. All verification outcomes must be documented.

### Local workspace data

Workspace metadata is stored in `workspaces.sqlite3` under the operating system's per-user local application data directory in a `CodeArcheologist` subfolder (normally `%LOCALAPPDATA%\\CodeArcheologist` on Windows). No database server, model or API key is required to create a named workspace. Workspace names are not filesystem paths. Tests use isolated temporary data directories and temporary repository fixtures; they do not alter your real workspaces or source repositories.

The API requires a paired local browser session for listing/reading workspaces, and a CSRF-protected UUID idempotency key for creation. It currently displays at most the 100 newest workspaces with an explicit count if more exist; pagination and backup/restore remain separate deliverables. Avoid recording secrets in workspace names. To register a source, open a workspace in the paired browser and enter the absolute path of an existing local directory. Registration checks and records the path, but does not scan or execute it. A directory with a `.git` marker is labelled as Git; other directories are labelled as local folders. Registrations persist through restarts. Symlink/junction roots, network paths, and filesystem roots are rejected in this first slice. Source locations are plaintext metadata; do not include secrets in their names. Inside a registered source, click **View snapshots** and then **Capture read-only snapshot** to explicitly copy and fingerprint eligible source files. Exclusions include Git metadata, known secret filenames, common binary extensions, vendor files and generated caches. **There is no general secret scanner.** Review source privacy before capturing: ordinary source files may contain confidential values. Captured bytes stay in the per-user local application data directory under `snapshots/<snapshot-id>`. The app does not run imported project files, restore dependencies, or interpret README instructions. Source capture has bounded file sizes and counts; rejected attempts do not publish partial snapshots. The displayed fingerprint is a **filesystem content manifest**, not a resolved Git commit. Known concurrent path/handle race limitations and recovery gaps are documented in [snapshot ADR 0004](docs/decisions/0004-read-only-local-snapshot-foundation.md).

For the first deterministic inventory, choose **Inspect source inventory** next to a saved snapshot. It reads only the stored and hash-verified snapshot, never the current source folder. Language and file-role labels are inferred from filenames, not from parsing code. See [ADR 0005](docs/decisions/0005-snapshot-file-inventory.md).

For written call expressions, open **Explore C# evidence graph** and filter by **Invocation sites (candidate or unresolved)**, or select a method declaration to see its directly contained invocation expressions. Containment edges are `observed-syntax`; an invocation may also have a `candidate-callee` edge labeled **`provisional-local`**, selected by a reference-free synthetic Roslyn compilation, never actual project-verified binding. Unresolved invocations have no guessed target. Calls in lambdas and local functions remain associated with their file rather than falsely attributed to an outer method. See [ADRs 0012](docs/decisions/0012-csharp-invocation-sites.md) and [0013](docs/decisions/0013-provisional-callee-binding.md).

On any `candidate-callee` edge, the graph now shows static **shared**, **disjoint** or **unknown** project candidates, along with source, target and shared `.csproj` candidates (project SHA-256, inclusion basis and unresolved-rule count). This helps assess the synthetic match but **never upgrades it to a real project-verified method dependency**. See [ADR 0014](docs/decisions/0014-callee-project-candidate-context.md).

For syntax-observed declarations—including methods and properties that type links do not mention—filter the graph by **Parsed declarations (unreferenced)**. Existing referenced declaration nodes are reused when their saved source spans match. These nodes prove parsed source locations, not compiler symbols. See [ADR 0011](docs/decisions/0011-syntax-declarations-in-evidence-graph.md).

For the navigable evidence graph, click **Explore C# evidence graph** on a saved snapshot. Select a project, C# file or referenced declaration to follow candidate relationships with exact provenance and explicit uncertainty. See [ADR 0010](docs/decisions/0010-first-evidence-graph.md).

For evidence-qualified relationship context, click **Inspect link context** next to a saved snapshot. It combines the two prior analyses and distinguishes shared candidates, disjoint candidates, missing project candidates and unresolved targets; none imply a verified dependency. See [ADR 0009](docs/decisions/0009-project-link-evidence-context.md).

For candidate project memberships, click **Inspect C# projects** beside a saved snapshot. It shows file and project hashes, inferred membership reasons, and unresolved MSBuild rules. See [ADR 0008](docs/decisions/0008-static-project-membership.md).

For provisional links, click **Inspect C# type links** beside a saved snapshot; it displays source and candidate target anchors or unresolved references. See [ADR 0007](docs/decisions/0007-provisional-type-links.md). All source-local links are hypotheses until project membership, references and compiler settings can be established.

For the first parser-backed observations, click **Inspect C# syntax** next to a saved snapshot. Parse errors and configuration limits are shown explicitly; the initial request ceiling is 1,000 C# files / 32 MiB of C# text. See [ADR 0006](docs/decisions/0006-roslyn-syntax-only.md) for scope, source-span provenance and limitations.

See [workspace persistence decision](docs/decisions/0002-sqlite-workspace-metadata.md) and [local source registration decision](docs/decisions/0003-local-repository-registration.md).

See [AGENTS.md](AGENTS.md), [SECURITY.md](SECURITY.md) and [Phase 1 acceptance](docs/phase1-acceptance.md).

License: Apache-2.0.
