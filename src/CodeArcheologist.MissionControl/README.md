# Code Archeologist — Mission Control (MC3)

**Status:** MC3 connects Mission Control to the live white-label API (REST + SignalR, `docs/api/README.md`). It keeps the MC1 demo walkthrough, which stays clearly labelled **DEMO MODE** and never mixes with live data.

## Live mode (MC3)

1. **Allow this page's address.** Add `http://127.0.0.1:5175` to `%LOCALAPPDATA%\CodeArcheologist\client-origins.json`, then restart the API:
   ```json
   { "schemaVersion": "code-archeologist-client-origins-v1", "origins": ["http://127.0.0.1:5175"] }
   ```
2. **Connect.** Start the API and copy the pairing code from its window. Run `npm run dev`, open http://127.0.0.1:5175/, and under **Live API** enter the code.
3. **Choose what to investigate.** Pick or create a workspace, then register a local folder. Capture a snapshot (always an explicit button press) and choose an entry point. Press **Start investigation**, or watch an earlier run.

**What drives the screen:**
- **Agents** take their states only from the host execution projection (`gate-c6b-execution-projection-v1`). Each one shows the exact host status code (`running`, `accepted`, `failed` …).
- **The timeline** shows only committed ledger events.
- **Delivery:** the events arrive over SignalR. On subscribe, on every reconnect, on any sequence gap, and after a terminal attempt event, the full ledger and projection are re-read over REST. Nothing is missed or shown twice (rule 59).
- **Commands** (create, register, capture, start, cancel) go only over REST, each with an `Idempotency-Key`. Cancel sends `expectedAttemptNumber`. SignalR is used only to subscribe.

**Security:**
- The bearer token lives only in memory. It is never stored in browser storage or cookies, and it ends when the tab closes or the 30-minute lifetime ends.
- REST sends the token as a header. Browsers cannot set WebSocket headers, so the SignalR hub connection carries it as the `access_token` query value, as the realtime contract specifies.
- Only loopback API addresses are accepted, and only GUIDs reach URL paths.

**Findings (MC4):** the **Findings** page shows the host-recorded report for the selected run:
- the Investigator's hypotheses, each with its host-reconciled verdict (the host disposition is authoritative, and never a verified fact), stated uncertainty and the Reviewer's reasoning;
- the Skeptic's objections;
- what each agent said it does not know.

All AI-written text is shown as plain text and labelled. Clicking a citation shows bounded lines (±8) from the **saved, hash-verified snapshot**, using the API's `code-archeologist-source-v1` resource. It never reads your live folder.

**Truthfulness:** an *accepted* assessment passed the host's contract checks; it is not proof its claims are true. Entry points are written candidates, and an empty list is *unknown*, not "no endpoints".

## MC1 visual foundation

All stage events in the demo walkthrough are synthetic and prominently labelled as such.

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

`npm run test:live` runs the real adapter end to end against a disposable API process. It needs the Release API build (`dotnet build src/CodeArcheologist.Api -c Release`). The run is cancelled at once, so it needs no Ollama.

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
- **MC3 (done):** live API adapter and console. See *Live mode* above.
- **MC4:** real activity/evidence view, historical event playback and trustworthy state projection.
