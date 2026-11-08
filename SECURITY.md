# Security Policy

Code Archeologist is an early-stage development project. The local-development shell now has a pairing and cookie-session mechanism. It is **not** a secure repository importer or production application. Do not expose it on a network or use it for sensitive repositories.

To report a vulnerability, use the repository's **Report a vulnerability** / private GitHub Security Advisory channel where enabled. If it is unavailable, ask a maintainer to enable a private reporting channel without publicly disclosing exploit details.

## Security commitments

- Default analysis never executes imported source, MSBuild targets, dependency lifecycle scripts or tests.
- Localhost binding alone is not sufficient authorization. This development shell requires console pairing, authenticated session cookies, explicit Host/Origin checks and CSRF headers for unsafe methods. Local runtime checks for pairing passed in PR #2. PR #3 adds only authenticated SQLite workspace **metadata** creation, with CSRF and idempotency checks; no repository-import or agent-command routes are present. This is not proof of a fully hardened browser security surface.
- Plugins are executable code; no assumed sandbox guarantee from in-process loading or process separation alone.
- No external LLM calls or source disclosure by default; no raw credentials in repository evidence or logs.
- Evidence provenance, safe snapshots, reversible vault reconciliation and user permissions are independent hard gates.

Workspace names and timestamps are stored in a local SQLite database, which is **not encrypted at rest** by Code Archeologist. Do not place passwords, tokens or other secrets in workspace names; access depends on the local operating system account and file permissions. Security/workspace tests use disposable directories instead of production app data.

This policy distinguishes **implemented design controls** from unverified security guarantees. Do not expose the developer shell remotely; hosted TLS/auth, credential storage, broader browser-surface review and plugin sandboxing remain out of scope. Process console output and environment variables are trusted only to the extent the local operating-system account is trusted. See the design specification and phase-one acceptance checklist.
