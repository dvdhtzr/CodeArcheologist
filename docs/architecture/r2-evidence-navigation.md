# R2 PR2 — Atlas Evidence Navigation

**Baseline:** merged R2 PR1 at `b91d28c7`. **Status:** Draft implementation requiring owner-run GREEN; not a release claim.

## Purpose

Let developers trace exact graph relationships from Architecture Atlas without losing their place on the canvas. Evidence inspection is subordinate to the authoritative saved-source and graph API, not a new reasoning or dependency engine.

## Existing data contracts reused

- C# saved-source evidence graph `csharp-snapshot-evidence-graph-v8`: exact node/edge IDs, from/to direction, kind, status, file hashes, line/column, explanation, project context and static project candidates.
- Authenticated `GET /source`, contract `code-archeologist-source-v1`: snapshot ID/fingerprint, file path + SHA-256, bounded line context, rehashed saved bytes, status and limitation caveat.
- Existing one-hop direct impact API and Inspector tab. No AI inference, graph mutation, code execution or new backend endpoint.

## Acceptance conditions

1. **In-place evidence inspector.** Select a graph edge or the exact edge from the accessible relationships list. The right pane displays the edge ID, original from/to graph node IDs and source labels, original relationship kind, exact backend evidence status, project-context diagnostic, file path/SHA-256/line/column, and backend-written explanation. Unknown statuses stay labeled unknown, never observed.
2. **Project hypotheses.** For an edge, three explicit lists independently identify source, target and shared static project candidates, including inclusion basis, recorded unresolved-rule count and saved project SHA-256. Missing/null project evidence is clearly distinct from a supplied empty candidate list. These are not evaluated MSBuild inputs.
3. **Inline saved-source inspection.** Clicking the source action requests the existing API endpoint on the exact selected snapshot and source anchor. The client checks `code-archeologist-source-v1`, untrusted-source status, snapshot ID, fingerprint, file path, SHA-256, exact focus line, bounded and sequential source-line response; rejects mismatches rather than displaying them as verified. The source is inert escaped React text, with focus-line highlight and explicit caveats, never the mutable registered folder.
4. **Exact endpoint navigation.** "Go to source node"/"Go to target node" focuses the selected saved node ID. Back restores the prior graph center, expansion seeds and selected edge. History is in-memory and limited to 32 steps; changing repository/snapshot clears it. No fabricated graph paths or inferred method calls.
5. **No lost verification.** Existing Architecture Atlas, Evidence Graph, direct-impact and Snapshot Coverage routes remain functional. PR2 does not alter the graph service, worker, SQLite, models, analyzer contracts or project file ingestion.

## Tests

- `tests/atlas-evidence.test.mjs` verifies valid source shape and rejects snapshot/fingerprint/hash/line/contract/line-range mismatches. Also checks exact-ID-only navigation, bounded history and selected-edge restoration.
- Existing real Chromium Playwright fixture opens an actual saved `declared-in` edge, fetches and highlights the corresponding rehashed source line, exercises project-context presentation, navigates to the target node, and uses Back to restore the selected edge without stale source.
- Standard `scripts/check.ps1` remains the authoritative GREEN gate. Stop local Vite and API server processes (ports 5175 and 5127) before running. Existing model-free R1 and R2 PR1 checks must remain green.

## Defer to PR3

High-volume graph navigation performance, responsive-layout/accessibility tuning, comprehensive screen-reader testing, representation calibration against larger source corpora, and persistent deep-link URLs are out of scope. All Atlas navigation in this PR is strictly session-local and read-only.
