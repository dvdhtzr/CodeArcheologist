# R2 PR3 — Atlas Visual Polish, Accessibility & Performance

**Baseline:** merged PR #59 (`14adf6db`). **Status:** Draft; requires owner Windows GREEN **and a visual walkthrough** before merging. No backend, agent, source inference, SQLite, source contract or hosted service changes.

## Boundaries adopted from the approved R2 review

1. **Layout:** Atlas alone has a wider workspace. Search/navigation can be collapsed. The evidence panel is keyboard-resizable with a 280–540px range (20px steps), and stacks under the canvas at smaller widths. Other Mission Control views keep their existing layout.
2. **Viewport and continuity:** On-demand initial graph load fits once. Ordinary edge/node inspection or one-hop expansion never auto-fits. Explicit search/navigation centers its actual saved graph node; Back restores the prior finite viewport (x, y, zoom), center, expansion and selection. "Fit visible graph" and "Project/file overview" are explicit, intentional fit actions. On a new Dagre layout, one shared graph node position anchors the new visible layout to avoid gratuitous global jumps; positions are visualization-only, not evidence.
3. **Accessibility:** Clearer focus styles, larger readable source text, native labeled/keyboard-operated range control, collapsible-search ARIA state, low-motion durations on a reduced-motion preference, existing React Flow keyboard navigation plus full-graph search and an accessible saved-relationship list. Project/candidate/unresolved statuses remain separate, with text plus distinct line patterns rather than relying on color alone.
4. **Performance:** Index lowercased node search text and exact edge identity once per snapshot. Full loaded-graph search remains available even when only 80/150 nodes are drawn. The default model-free stress test enforces 10,000 saved nodes/16,000 edges and 150 nodes/250 edges displayed, without changing actual graph/analyzer budgets. Duplicate backend edge IDs fail closed.
5. **Evaluation:** A repeatable **synthetic** benchmark measures index, repeated search, focused projection and Dagre on 10k/16k, emitting JSON timings and process/runtime metadata. Times are **observations**, not guaranteed thresholds, and must not be represented as real-world repository results. Optional browser acceptance on an **owner-acquired pinned C6c repository** verifies actual fingerprint, graph provenance, bounded display and records API+render load elapsed time.

## Do not interpret appearance as architecture proof

- Source nodes, edges and statuses come only from the authoritative saved C# graph.
- Dagre grouping/coordinates do not prove dependencies, compilation, runtime execution, or project file membership.
- Source inspection remains hash-verified against the exact saved snapshot; no live source file reads, compilation or execution.
- An HTTP 413 produces the same honest unavailable state and independent Snapshot Coverage as R2 PR1/2; the synthetic benchmark does not override backend 10k/16k limits.
- The pinned curated checkouts are externally acquired using existing owner scripts and are never downloaded or run by the Atlas.

## Default acceptance gate (Windows)

Stop local API/Vite servers on ports 5127 and 5175, then:

```powershell
git fetch origin
git switch feature/r2-atlas-polish-performance-pr3
git pull --ff-only
npm --prefix .\src\CodeArcheologist.MissionControl run typecheck
npm --prefix .\src\CodeArcheologist.MissionControl run test
npm --prefix .\src\CodeArcheologist.MissionControl run test:browser
.\scripts\check.ps1
```

The expanded regular Playwright journey covers keyboard sizing, hide/show search, no camera movement on evidence selection, Back viewport restoration, mobile stacked layout, real saved source and original PR1/2 interactions. Existing GREEN remains required.

## Measured synthetic baseline (no model, no database)

```powershell
npm --prefix .\src\CodeArcheologist.MissionControl run benchmark:atlas
```

Outputs `atlas-synthetic-benchmark-v1` JSON to stdout with sample/median/min/max durations for indexing, full search, focused projection and Dagre and a heap snapshot. Record the exact run date, Windows hardware, Node version and output before declaring any numerical performance acceptance. Neither browser FPS nor end-to-end API latency can be inferred from a Node benchmark.

## Opt-in pinned real-repository browser acceptance

Only if an existing C6c curated checkout is available **outside** the Code Archeologist repository:

```powershell
$env:CODEARCHEOLOGIST_ATLAS_CURATED_ROOT = 'C:\Path\To\PreviouslyAcquiredCurated'
$env:CODEARCHEOLOGIST_ATLAS_CURATED_ID = 'R01'
npm --prefix .\src\CodeArcheologist.MissionControl run test:browser -- --grep 'Atlas optional pinned curated'
Remove-Item Env:CODEARCHEOLOGIST_ATLAS_CURATED_ROOT
Remove-Item Env:CODEARCHEOLOGIST_ATLAS_CURATED_ID
```

The opt-in spec skips itself in normal checks. It reads `eval/curated/repositories.json` (R01 default), requires the actual API snapshot fingerprint match the fixed manifest, loads the actual C# graph in a fresh disposable API database, confirms the rendered edges all exist in that saved graph, focuses a real declaration and attaches an `atlas-curated-browser-metrics.json` report with provenance and an end-to-end load duration. The test never builds, runs or modifies the curated code. Changing the pinned checkout fails the fingerprint check.

## Owner visual walkthrough — mandatory for R2 completion

- At desktop width (~1920px), inspect the Atlas with the search panel expanded and collapsed; verify the graph is readable, the inspector can be resized, and source context has enough room.
- Search for a declaration, inspect an edge, then expand a neighbor: no unexpected auto-fit; existing shared node position and viewport remain intelligible.
- Follow edge endpoints and Back at different zoom levels: camera and selected evidence restore; no old source content leaks across selections or snapshots.
- Navigate with keyboard only: search panel toggle, range slider, search/result buttons, relationship choices, React Flow nodes, source/target actions; visible focus, no mouse requirement.
- At ~820px and ~390px, inspect stacking and overflow without loss of source/context or inaccessible actions.
- Enable reduced motion in OS/browser and repeat navigation: no forced animated camera movement.

**Deferrals:** new semantics, inferred dependencies, user-authored graph edits, whole-graph virtualization over the 10k limit, new graph endpoints, cross-repository interpretation, formal third-party accessibility certification. These require a separately approved milestone.
