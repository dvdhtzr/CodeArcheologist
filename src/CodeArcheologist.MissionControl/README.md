# Code Archeologist — Mission Control (MC1)

**Status:** standalone visual foundation / design preview. **No API calls, model calls, repository reads, database access or SignalR connections happen in MC1.** All stage events in the walkthrough are synthetic and prominently labeled as such.

Mission Control is an *independent frontend application in the same Code Archeologist monorepo*. It is not a refactor of `CodeArcheologist.Client` and is not coupled to the C6 worker, MAF adapters or SQLite. The existing frontend remains untouched and continues to serve as a regression harness.

## Develop

Requires Node.js 22.12+ and npm. From the repository root:

```powershell
npm ci --prefix .\src\CodeArcheologist.MissionControl --no-audit --no-fund
npm --prefix .\src\CodeArcheologist.MissionControl run test
npm --prefix .\src\CodeArcheologist.MissionControl run typecheck
npm --prefix .\src\CodeArcheologist.MissionControl run build
npm --prefix .\src\CodeArcheologist.MissionControl run dev
```

Open **http://127.0.0.1:5175/**. Stop with Ctrl+C. The original developer frontend remains at 5174; MC1 uses 5175 so they can run side by side.

No Ollama, .NET process, database, pairing code or secrets are required for the preview.

## What to inspect

- **Agent Observatory** — three original human-like SVG character avatars, with distinct cyan / amber / violet identities, selection, responsive stage layout and animated (CSS-only) activity halos.
- **Visual walkthrough** — play, pause, next, reset. Progress is *walkthrough position*, not evidence quality or research completion.
- **Agent spotlight** — click any avatar for role description, contract boundaries and visible demo events.
- **Activity Timeline** — illustrative event chronology using real *names* from the established event taxonomy, **not** genuine backend records.
- **Design Principles** — independent evidence sessions, host-owned truth, future transport and replaceable clients.
- **Accessibility** — keyboard-native buttons, visible focus, explicit text status, reduced-motion preference, narrow/mobile layout.

## Architecture and trust boundary

```text
CodeArcheologist.Api  <---- future versioned REST + SignalR ---->  MissionControl
  C6 + MAF + SQLite                                            React + TypeScript
  authoritative event ledger                                  presentational projection
  authentication + scope                                      original avatar vectors
  execution/recovery                                          effects / inspectable UI
```

MC1 uses `src/demo.ts` for all placeholder activity. The demonstration sequence is faithful to today's **sequential** Gate C execution: verified scope → Investigator → independent Skeptic → Reviewer. The Investigator and Skeptic do **not** exchange conclusions; their accepted assessments are separately delivered to the Reviewer. There is no invented agent conversation.

The backend remains the source of truth. In MC3 the new client will consume the **versioned public API contract** (ADR 0021 / W1 when merged), including authenticated REST operations, SignalR `eventAppended` and `executionUpdated` messages, deduplication by event ID, reconnection and state reconciliation. Visual effects must be triggered by committed host events, not invented model thoughts, timers or speculation. No backend transport is added here.

**Security:** the new preview binds to 127.0.0.1. It does not store credentials. MC3 must coordinate allowlisted loopback origins, bearer-token lifecycle and local application authorization with the API design, rather than bypass the existing guard.

## Independent verification

`npm run test` uses the Node.js type-stripping flag for a dependency-free check of the demo's stage semantics, event ordering, backend isolation, and accessibility markers. `npm run typecheck` and `npm run build` validate the separate frontend itself. A human browser pass is still required to judge rendering, focus behavior, motion, responsive layout and original character quality.

This PR deliberately **does not modify**:

- `src/CodeArcheologist.Api/**`
- `src/CodeArcheologist.Client/**`
- `scripts/check.ps1` / `scripts/check.sh`
- Any C6 architecture, worker, persistence or tests

The original repository checks remain independently applicable. No PR is auto-merged.

## Proposed follow-on PRs

- **MC2:** refine original portraits, connection animation, compact layout, character spotlight and visual interaction under real browser acceptance.
- **MC3:** add an independent API transport client only after the white-label API contract has been merged and verified. No fake live status.
- **MC4:** real activity/evidence view, historical event playback and trustworthy state projection.
