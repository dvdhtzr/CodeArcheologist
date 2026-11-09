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
