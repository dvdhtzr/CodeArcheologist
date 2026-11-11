# R1 primary application closeout gate

**Status:** Implementation proposed on R1 PR3; **not accepted until the owner reports GREEN on the exact PR head**. R1 completion is a product-integration gate, not a release of Code Archeologist v1.0.

## Objective

Make Mission Control the normal local developer experience without false certainty, losing operational recovery, or hiding what a saved source snapshot did *not* include. Retain the legacy client as an explicit advanced-diagnostics fallback; do not migrate every specialist inspector into R1.

## Acceptance decisions

| R1 property | Implementation contract | Verification |
| --- | --- | --- |
| Single primary shell | `scripts/start.ps1` and `start.sh` start Mission Control on 5175; selected workspace/repository/snapshot also drives architecture exploration | Existing Playwright end-to-end |
| Deterministic exploration | Authenticated `entry-points`, `methods`, `impact`, `csharp-evidence-graph`, and saved `source` are candidate/observed/provisional as reported, never promoted | Existing graph unit and browser suites |
| Snapshot coverage | Independent `GET snapshot` + `manifest` + `inventory`, matched on exact IDs, fingerprint, included counts/bytes/files, exclusions and classifier; classify filenames only | New provenance unit tests and Playwright real ignored-directory test |
| Graph limits | A 413 graph/candidate failure does not prevent access to capture coverage or saved investigation history | Inspection of independent route/client and owner browser acceptance |
| Host-owned recovery | `execution.recovery` (eligibility, diagnostic, expected attempt number) is the sole UI authority. Continuation is explicit, uses a stable retry UUID and original run scope. No automatic restart or interpretation of model prose | Client contract tests; existing host checkpoint/continuation tests; manual eligible-interruption browser inspection when available |
| Expiry and revocation | 401 on any authenticated call clears the shared connection and selected run; server expiry timestamp also clears it. No browser storage; explicit re-pair option | Client session-loss unit test and real bearer-revocation Playwright test |
| Snapshot switching | Old live run subscriptions and selected run are cleared on scope switch; stale async graph/coverage results cannot replace the new scope | Code invariants, graph/coverage browser tests |
| Regression suite | Windows `scripts/check.ps1` gates backend, security, React builds, typecheck, model-free host contracts and headless Chromium browser tests | Owner-reported GREEN; no GitHub CI |

## R1 / R2 boundary

**Explicitly retained in the legacy developer explorer:** low-level syntax diagnostics, stand-alone provisional type links, static project membership and link context, advanced agent evidence-handle diagnostics and citation-check demonstration. The primary UI already exposes graph provenance and saved-source citations; duplicating every specialized legacy panel would delay visual architecture work without improving the main workflow.

**Deferred to R2:** repository-scale interactive architecture graph and navigation, richer workflow/dependency visual design, and visual coverage explanations. Do not invent transitive, cross-repository, effective HTTP route, execution, or compilation claims.

**Later release work, not R1:** portable packaging, backup/restore, multi-language deep analysis, Obsidian reconciliation, plugin host, sandboxed execution, AI quality calibration, and public release hardening.

## Residual risk and release honesty

- Real Ollama / MAF model runs and semantic validity remain a separate evaluation concern; model-free GREEN cannot establish reliable behavioral understanding.
- The UI's positive continuation button uses host-reported recovery eligibility. The browser suite deliberately does *not* fabricate an interrupted run or call an LLM merely to make continuation eligible. The existing backend validation and client contract tests cover the endpoint and guarded command shape; owner should validate a genuine interrupted run when one is available.
- Chromium Playwright checks are a narrow disposable-source workflow, not stress testing, accessibility conformance or Windows/Linux/macOS distribution verification.
- Source inventory classifies file paths only. An excluded directory reports a subtree exclusion, not every file inside it. Neither snapshots nor graph results establish secrets absence or repository completeness.
- Runtime endpoints, dependency relationships and compilation remain unverified unless independently established by future evidence.

**Decision rule:** After the owner reports a complete local GREEN on PR3 and the stated manual eligibility check has either been completed or explicitly recorded as pending, recommend closing R1 as **product-integrated with documented advanced-diagnostics fallback**. Do not claim the project is ready for a v1.0 release.
