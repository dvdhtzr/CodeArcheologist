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
