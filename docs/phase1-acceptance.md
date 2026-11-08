# Phase 1 Acceptance Checklist

**Current development scope:** PR #1 established the developer shell and architecture baseline. PR #2 added localhost browser pairing and negative security tests. PR #3 adds SQLite workspace **metadata** and an explorer. None is a complete investigative product or a public preview.

## Initial slice: pending independent verification

- [x] Backend compiles with .NET 10 SDK (owner-reported Windows local verification).
- [ ] GET /api/v1/health serves non-sensitive JSON over loopback only.
- [x] React/TypeScript client typechecks and bundles successfully (owner-reported Windows local verification before switching to npm ci).
- [ ] UI connectivity state reflects a real backend request, never fabricated agent progress.
- [ ] Windows PowerShell and POSIX scripts launch and verify trusted application source correctly.
- [ ] No sensitive or mutating API, code-import process execution, or plugin loader is exposed.
- [x] Frontend dependency lockfile structurally reviewed and committed (valid JSON, matching direct manifest pins and package integrity fields). 
- [ ] Clean `npm ci` install and subsequent typecheck/build verified after script changes.
- [ ] Dependency vulnerability and licensing review completed before preview releases.
- [ ] No GitHub Actions CI required.

The checked build results above reflect owner-reported Windows verification; later changes to the install scripts still require a new run. Other checks remain unchecked until observed.

## PR #2 local-access security checks (pending owner-run verification)

- [ ] Local test builds and runs from scripts/check.ps1 on Windows without errors or warnings.
- [ ] Health works over loopback, while a forged Host header is rejected.
- [ ] Unauthenticated session reads receive HTTP 401.
- [ ] Pairing rejects incorrect codes, disallowed Origins and missing custom request headers.
- [ ] Valid pairing sets a session cookie with HttpOnly and SameSite Strict.
- [ ] Unauthenticated or missing/invalid-CSRF mutations are rejected.
- [ ] Log out revokes the previous session even if the old cookie is replayed.
- [ ] Developer browser flow displays pairing code entry and sign-out; launch scripts show the ephemeral code.
- [ ] No sensitive workspace endpoints are reachable before the next security review.

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

## Next foundation gates

1. Secure local browser bootstrap, authentication, origin/host and anti-CSRF controls with malicious-origin tests.
2. SQLite workspace and event-state persistence, schema migrations and consistent backups.
3. Bundled React assets with a self-contained .NET launcher and setup wizard.
4. Clean-machine validation on Windows x64, Linux x64, macOS ARM64 and x64.
5. Safe snapshot ingestion with malicious repository/project fixture tests; degraded semantic analysis must be explicit.

The product's Phase 1 is not complete when the first PR merges. Preserve all v0.1 and v0.2 acceptance gates. 
