# Code Archeologist

**Status: Initial implementation foundation — not yet a functioning repository intelligence product.**

Code Archeologist aims to be a local-first, cross-platform software intelligence platform that analyzes one or more source repositories, constructs evidence-backed architecture and dependency models, supports adaptive multi-agent investigation, and preserves institutionally reviewed knowledge in an Obsidian-compatible vault.

The approved architectural baseline consists of [v0.1](docs/architecture/Code-Archeologist-v0.1.md), the [v0.2 technical design](docs/architecture/Code-Archeologist-v0.2.md), and the [subsequent owner-approval record](docs/decisions/0001-architecture-approval.md). Historical status wording in the original documents is preserved intentionally.

## What this first PR adds

- ASP.NET Core .NET 10 API bound only to loopback and exposing a public health endpoint.
- React + TypeScript development client that reports actual API health.
- Local Windows PowerShell and Unix shell startup/build scripts.
- Safety, contribution, acceptance and architecture documentation.

**Not yet implemented:** repository import, local auth, SQLite, agents/MAF, quality engine, plugins, source sandbox, graph, vector retrieval, vault reconciliation, production packaging, or complete Mission Control. No user-repository content should be loaded into the current shell.

## Development

Requires a .NET 10 SDK and Node.js 22.12+ with npm **for working on the source**. Packaged releases are intended to eliminate those end-user prerequisites.

Windows: run scripts/check.ps1 then scripts/start.ps1.
macOS/Linux: run bash scripts/check.sh then bash scripts/start.sh.
Open http://127.0.0.1:5174/ ; the API is on http://127.0.0.1:5127/ and supports GET /api/v1/health.

The npm lockfile is not yet committed; exact dependency versions are listed, but a reviewed package-lock.json must be added before claiming reproducible builds. GitHub CI is intentionally absent. All local verification outcomes must be documented.

See [AGENTS.md](AGENTS.md), [SECURITY.md](SECURITY.md) and [Phase 1 acceptance](docs/phase1-acceptance.md).

License: Apache-2.0.
