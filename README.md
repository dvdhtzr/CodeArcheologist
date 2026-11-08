# Code Archeologist

**Status: Early local-first development. Workspaces and registered repository paths persist locally; source ingestion and intelligence are not implemented yet.**

Code Archeologist aims to be a local-first, cross-platform software intelligence platform that analyzes one or more source repositories, constructs evidence-backed architecture and dependency models, supports adaptive multi-agent investigation, and preserves institutionally reviewed knowledge in an Obsidian-compatible vault.

The approved architectural baseline consists of [v0.1](docs/architecture/Code-Archeologist-v0.1.md), the [v0.2 technical design](docs/architecture/Code-Archeologist-v0.2.md), and the [subsequent owner-approval record](docs/decisions/0001-architecture-approval.md). Historical status wording in the original documents is preserved intentionally.

## Current developer milestones

- ASP.NET Core .NET 10 API bound only to loopback, exposing a public health endpoint and a protected local browser session.
- React + TypeScript development client with real API health, local browser pairing and a persistent workspace explorer.
- SQLite-backed workspace creation, listing and reopening across app restarts, protected by local pairing and CSRF checks.
- Authenticated registration of multiple existing local Git working trees or ordinary directories per workspace. This saves path metadata only; it does **not** read, build, scan or execute their contents.
- Local Windows PowerShell and Unix shell startup/build scripts with disposable security and workspace persistence tests.
- Safety, contribution, acceptance and architecture documentation.

**Not yet implemented:** actual repository ingestion/snapshots, complete workspaces (vault mapping, backup/restore), production-grade hosted authentication, agents/MAF, quality engine, plugins, source sandbox, graph, vector retrieval, vault reconciliation, production packaging, or complete Mission Control. No user-repository content should be loaded into the current shell.

## Development

Requires a .NET 10 SDK and Node.js 22.12+ with npm **for working on the source**. Packaged releases are intended to eliminate those end-user prerequisites.

Windows: run scripts/check.ps1 then scripts/start.ps1.
macOS/Linux: run bash scripts/check.sh then bash scripts/start.sh.

**Local pairing:** the start script prints a new 32-character pairing code. Enter it in the browser to unlock the current process-scoped local session. The API uses an HttpOnly SameSite=Strict cookie, a server-side revocation list and a CSRF header for state-changing requests. Sessions expire after 30 minutes and are invalid after restarting the API. The code is shown only in local terminal output and must not be shared. The pairing code can be retried until the API stops, with temporary lockouts after repeated failures. Pairing is for localhost-only development, **not remote or multi-user access**.
Open http://127.0.0.1:5174/ ; the API is on http://127.0.0.1:5127/ and supports GET /api/v1/health.

The committed `package-lock.json` pins the frontend dependency graph; local verification uses `npm ci` to reproduce it. Previously successful Windows developer verification does not establish cross-platform packaging. GitHub CI is intentionally absent. The check scripts also run security and workspace integration tests against the trusted API with isolated, temporary SQLite databases. Stop the development server before running the check script because both use API port 5127. All verification outcomes must be documented.

### Local workspace data

Workspace metadata is stored in `workspaces.sqlite3` under the operating system's per-user local application data directory in a `CodeArcheologist` subfolder (normally `%LOCALAPPDATA%\\CodeArcheologist` on Windows). No database server, model or API key is required to create a named workspace. Workspace names are not filesystem paths. Tests use isolated temporary data directories and temporary repository fixtures; they do not alter your real workspaces or source repositories.

The API requires a paired local browser session for listing/reading workspaces, and a CSRF-protected UUID idempotency key for creation. It currently displays at most the 100 newest workspaces with an explicit count if more exist; pagination and backup/restore remain separate deliverables. Avoid recording secrets in workspace names. To register a source, open a workspace in the paired browser and enter the absolute path of an existing local directory. The path is checked and recorded, not scanned or executed. A directory with a `.git` marker is labelled as Git; other directories are labelled as local folders. Registrations persist through restarts. Symlink/junction roots, network paths, and filesystem roots are rejected in this first slice. Source locations are plaintext metadata; do not include secrets in their names. See [workspace persistence decision](docs/decisions/0002-sqlite-workspace-metadata.md) and [local source registration decision](docs/decisions/0003-local-repository-registration.md).

See [AGENTS.md](AGENTS.md), [SECURITY.md](SECURITY.md) and [Phase 1 acceptance](docs/phase1-acceptance.md).

License: Apache-2.0.
