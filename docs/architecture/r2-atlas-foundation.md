# R2 PR1 — Architecture Atlas Foundation

**Baseline:** R1 merged at `5e323892`. **Status:** Proposed draft PR; not accepted until the owner runs the complete local GREEN gate.

## Approved architecture and implementation scope

- **Renderer:** React Flow 12 (locked to 12.11.6), with Dagre (locked to 1.1.8). These are MIT-licensed dependencies. Dagre supplies deterministic node positions, not architectural inference.
- **View modes:** Default project/file overview and deliberate source-node-focused neighborhood. Use the existing Architecture Explorer and API pairing/snapshot selection.
- **Evidence:** Only `csharp-snapshot-evidence-graph-v8` and the exact selected snapshot's fingerprint. Every edge retains its saved ID, direction, kind and evidence status. No generated nodes, synthetic clusters, reverse dependencies or inferred transitive edges.
- **Search:** Full successfully loaded graph, independent of the visible bounded view; node-kind filter, results-count/preview cap.
- **Limitations:** Clear distinction between eligible and actually displayed nodes and edges, full loaded graph counts, source limitations and 413 failures. Snapshot Coverage remains an independent way to understand capture scope.

## Exact display bounds

| View | Source node policy | Edges | Maximum |
| --- | --- | --- | --- |
| Overview | Saved `project-file` nodes, then saved `csharp-file` nodes prioritizing files with candidate membership | Exact `candidate-compile-item` relationships between both visible endpoints | 80 nodes, 250 edges |
| Focus | Center node and one-hop incoming/outgoing edges; additional visible nodes may be deliberately expanded | Original exact graph edges in recorded direction; no generated transitive edges | 150 nodes, 250 edges, at most 12 additional expansion seeds |
| Search | Exact saved node labels, IDs, source paths, kinds and details, case-insensitive | No inferred relationships | First 80 matching nodes, total hit count shown |
| Source inspection | Use existing authenticated saved-source endpoint and exact path, file SHA-256, line | Original selected edge provenance | No mutable repository file reads |

Graph API hard limits remain **10,000 nodes / 16,000 edges**; they are *not* rendering budgets. Failure or 413 produces an unavailable state, **never a graph that looks complete but represents zero nodes**.

## Evidence encoding

| Saved status | Treatment |
| --- | --- |
| `observed-syntax` | Solid cyan |
| `observed-location` | Solid blue |
| `candidate` | Amber dashed |
| `provisional-local` | Violet dashed |
| `unresolved` | Gray dotted |
| New/unknown status | Gray dotted with explicit unrecognized-status label; never treated as observed |

React Flow's node positions, arrows and animated viewport do not add semantic meaning. In particular a `candidate-callee` from **invocation site to method declaration** is *not* a verified method-to-method call, and project inclusion candidates are *not* compiled inputs.

## Implementation boundaries

- Zero backend/API/SQLite/MAF/Ollama/legacy-client changes. All additional dependencies belong exclusively to the React Mission Control frontend.
- Pure, renderer-independent `atlasModel.ts` provides indexing, deterministic projection, limits and search. Layout and React Flow event handling remain in `ArchitectureAtlas.tsx`.
- Loading is explicit and on-demand; not triggered by switching from Observatory to Explorer. Scope change discards in-flight requests, selected nodes, expansion seeds, search and viewport state.
- Existing **Candidates & direct impact**, **Evidence graph**, and **Snapshot coverage** tabs remain intact and independent.

## Owner acceptance

Run the existing `scripts/check.ps1` on the PR branch with both ports free. This includes TypeScript typecheck, existing R1 tests and expanded headless Chromium tests using a disposable C# source folder:
1. Mission Control selects a real saved snapshot, loads the Atlas and displays real API-reported graph counts and fingerprint.
2. Overview contains only saved project/file source nodes and candidate membership links, not invented architectural components.
3. Full graph search locates a method omitted from the overview; selecting it produces a bounded direct neighborhood.
4. Selecting a neighboring node can deliberately expand its exact saved direct edges. Display bounds and relationship statuses are explicit.
5. Switching snapshots discards prior Atlas state; existing graph, impact, findings, coverage and session-repair tests continue to pass.

## Not included in PR1

PR2 will improve the persistent evidence inspector, line-specific edge drill-down, and richer navigation / deep links. PR3 will address responsive polish, keyboard/a11y, representative large corpus measurements and scalability calibration. No semantic pipeline or inferred relationships are approved here.
