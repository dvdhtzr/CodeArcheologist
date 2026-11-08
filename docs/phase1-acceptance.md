# Phase 1 Acceptance Checklist

**Current first PR scope:** developer-only .NET 10 and React shell plus architecture/governance baselines. This is *not* a working investigative product or a public preview.

## Initial slice: pending independent verification

- [ ] Backend compiles with .NET 10 SDK.
- [ ] GET /api/v1/health serves non-sensitive JSON over loopback only.
- [ ] React/TypeScript client typechecks and bundles successfully.
- [ ] UI connectivity state reflects a real backend request, never fabricated agent progress.
- [ ] Windows PowerShell and POSIX scripts launch and verify trusted application source correctly.
- [ ] No sensitive or mutating API, code-import process execution, or plugin loader is exposed.
- [ ] Frontend dependency lockfile generated, audited and committed for reproducible builds.
- [ ] No GitHub Actions CI required.

Checks remain unchecked until they have actually been run and independently confirmed.

## Next foundation gates

1. Secure local browser bootstrap, authentication, origin/host and anti-CSRF controls with malicious-origin tests.
2. SQLite workspace and event-state persistence, schema migrations and consistent backups.
3. Bundled React assets with a self-contained .NET launcher and setup wizard.
4. Clean-machine validation on Windows x64, Linux x64, macOS ARM64 and x64.
5. Safe snapshot ingestion with malicious repository/project fixture tests; degraded semantic analysis must be explicit.

The product's Phase 1 is not complete when the first PR merges. Preserve all v0.1 and v0.2 acceptance gates. 
