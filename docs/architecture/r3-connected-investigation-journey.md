# R3 PR2 — Connected Investigation Journey

**Status:** Draft implementation; owner-local verification required.
**Approved R3 architecture:** D1–D10. **Prerequisite:** R3 PR1 merged (#61, SQLite v7, immutable context).
**Scope:** Mission Control only. No backend migrations, new model prompts, new evidence authority, new agent tools, or changes to the C6b worker.

## Goal

Turn one narrowly supported developer journey into a coherent experience:

1. Pair with local API and choose an exact saved workspace/repository/snapshot.
2. Open the Architecture Atlas and select a graph node of kind `http-endpoint-attribute` or `http-minimal-mapping`.
3. Explicitly click **Investigate this HTTP candidate**. The browser calls the PR1 structured question endpoint with `questionKind=explain-http-entry-point` and a stable UUID idempotency key.
4. The host re-verifies the saved candidate, admits a fenced run, and returns the immutable versioned question context. The UI cross-checks run/question/scope/fingerprint/node/contract identities, without treating that check as an independent truth proof.
5. Move to the existing **Agent Observatory** and watch the host-authoritative C6b execution feed, or open **Findings**, then return to the exact Atlas selection.
6. Navigate between views without a second POST or a fabricated graph relationship. Preserve locally held Atlas query, selected node, evidence details, expansion/history, and viewport for the same active snapshot.

## Architecture boundaries

- Only the existing HTTP candidate kinds expose the action. Method syntax declarations, unresolved calls, files, arbitrary user questions and text fields do **not** authorize an investigation.
- The REST API already enforces the permission and the snapshot; the frontend rechecks the response's exact scope and question identity before reporting successful admission.
- One user click admits one task. A pending retry reuses its UUID until authoritative admission is received, so transient request failures do not inadvertently create a second run. A later explicit click following a successful admission starts a new request.
- The existing LiveConsole and LiveRun provide host-observed execution state. SignalR is notification only; REST is authoritative. No optimistic AI results are rendered.
- Navigation state is **ephemeral presentation state**, not investigation evidence and not persisted to SQLite, browser storage, or the model.
- The Explorer remains mounted while other pages are shown, but is hidden from view and accessibility when not active. It is cleared when its API connection or selected snapshot changes. The wide-canvas CSS applies only while the Explorer is visible.
- The `activeQuestion` record is held in memory for the current API session/snapshot. If the user disconnects, changes snapshot, closes the run, or selects another run, the visible question association is cleared. This is intentionally narrower than persistent post-repair navigation; historical v7 question context is available via the PR1 API but is not indexed into the UI in this milestone.
- The demo mode remains clearly synthetic. This feature is for the live API only.
- A report containing structured agent assessments is **not proof** of an effective HTTP route or runtime binding.

## Tests

`tests/question-journey.test.mjs` — model-free admission validator:
- Valid HTTP entry-point candidate and exact snapshot are accepted.
- Changed run ID, question ID, subject, question kind, fingerprint, contract, version or snapshot are rejected.
- Navigational selection is restricted to the original in-memory API session and snapshot.

`tests/browser/r3-question-journey.spec.mjs` — real Chromium + actual disposable API for pairing, repository capture, verified Atlas graph, node selection and return navigation:
- Only the **new POST admission and its fake run GET/event reads** are intercepted to avoid invoking local Ollama. Browser-visible responses are clearly fixture data; this is **not a real-model end-to-end AI test**.
- Exactly one admission for an explicit click; supported candidate chosen from verified Atlas.
- Navigation to Observatory and Findings; return to the original fingerprint, node and near-exact viewport.
- A syntax-declaration node does not offer the HTTP investigation action.
- No imported code execution, persistent user-folder changes, or real model calls.

The existing PR56 browser suite and all model-free C6b security and worker checks must continue to pass.

## Owner verification (Windows)

```powershell
git fetch origin
git switch feature/r3-connected-investigation-journey-pr2
git pull --ff-only
npm --prefix .\src\CodeArcheologist.MissionControl run typecheck
npm --prefix .\src\CodeArcheologist.MissionControl run test
npm --prefix .\src\CodeArcheologist.MissionControl run test:browser
.\scripts\check.ps1
```

Before running browser tests, stop any independent process already using port 5127 or 5175.

**GREEN is not claimed until local execution succeeds.** The optional curated test may skip. Real Ollama investigation quality is deliberately deferred to the R3 comparative evaluation gate, not simulated by these tests.

## Out of scope

New schema, new tools, adaptive model retrieval, persistent tab/browser navigation state, persistence of presentation viewport, arbitrary-method investigations, multi-repo workflows, packaged OS distributions, changes to role prompts or reasoning criteria, and semantic acceptance of model claims.
