# Security Policy

Code Archeologist is an early-stage development project. The initial shell is **not** a secure repository importer or production application. Do not expose it on a network or use it for sensitive repositories.

To report a vulnerability, use the repository's **Report a vulnerability** / private GitHub Security Advisory channel where enabled. If it is unavailable, ask a maintainer to enable a private reporting channel without publicly disclosing exploit details.

## Security commitments

- Default analysis never executes imported source, MSBuild targets, dependency lifecycle scripts or tests.
- Localhost binding is not sufficient authorization for sensitive API operations. Authentication, host/origin validation, anti-CSRF controls and XSS-safe rendering must be developed and verified before such routes exist.
- Plugins are executable code; no assumed sandbox guarantee from in-process loading or process separation alone.
- No external LLM calls or source disclosure by default; no raw credentials in repository evidence or logs.
- Evidence provenance, safe snapshots, reversible vault reconciliation and user permissions are independent hard gates.

These are requirements, **not** claims of currently implemented protections. See the design specification and phase-one acceptance checklist.
