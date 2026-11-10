# Gate C6c — Hybrid evaluation laboratory

Status: **C6c-1 is in implementation on a draft PR.** The decisions are approved (ADR 0020). No acceptance result exists yet. All 20 labels are assistant drafts awaiting owner sign-off, and the thresholds are still provisional.

## Slices

| Slice | Scope | State |
|---|---|---|
| C6c-1 | Case and label schema, corpus validator, budget declaration, isolated harness, 3 comparators, deterministic metrics, 20 fixtures | implemented; Linux self-test passed; owner verification and label sign-off pending |
| C6c-2 | 15 deliberate change experiments; 15 curated real repositories (owner-run acquisition, pinned SHA and fingerprint) | not started |
| C6c-3 | Blinded owner A/B usefulness judging; 10 held-out cases (owner-authored, sealed); repeatability trials | not started |
| C7 | Scored acceptance run, integrity audit, documented limitations, owner release decision | not started |

## Lab (`tests/evaluation-lab`)

| Command | Model | Purpose |
|---|---|---|
| `--validate-corpus` | none | Schemas, IDs, pinned fixture bytes, label shape, and budgets equal to the host constants |
| `--self-test` | none | 36 checks (below) |
| `--describe <case-dir>` | none | Prints the citable evidence locations used when authoring labels |
| `--run --out <dir> [--cases ...] [--comparators ...] [--provisional]` | genuine local Ollama for `gate-b` and `gate-c` | Runs and scores; writes per-evaluation report and score JSON plus `summary.json` |

### Isolation
For each case the lab:
1. copies the pinned fixture bytes, rechecking their SHA-256, into a new temporary source directory;
2. creates a disposable data directory;
3. registers, captures and builds the graph through the production services;
4. resolves the entry point by exact written-syntax location (exactly one match, or the case fails);
5. deletes everything afterwards.

The self-test verifies that the `eval/` tree is byte-identical after a run.

### Comparators (one shared report shape: `c6c-lab-report-v1`)
- **deterministic-baseline:** the verified evidence package restated without a model.
- **gate-b:** the existing `GateBInvestigator`.
- **gate-c:** the production C6b path: fenced admission, managed worker, `GateC6StageCoordinator` and `MafRoleStageRunner`, so checkpoints and validation are exactly as in the app.

Diagnostic-repeat cases get 1 + 2 Gate C trials (4 of the 12 are designated in C6c-1).

### Deterministic metrics (`c6c-lab-score-v1`)

The integrity blockers, all zero-tolerance:

| Blocker | Detection |
|---|---|
| Incorrect snapshot scope | Report snapshot, fingerprint, entry point or case ID differs from the captured case |
| Fabricated or foreign citation | Any citation not exactly equal (ID, file, SHA-256, line, status) to an item of the freshly verified package |
| Unsupported claim promoted to verified fact | `IsVerifiedFact` set, or a `StructurallySupported` recommendation |
| Silently discarded objection | A Skeptic concern missing, duplicated or foreign in the Reviewer's coverage |
| Failed workflow presented as completed | "Completed" without three validated roles, an accepted reconciliation and three checkpoints |
| Schema regression | Unsupported report schema version |

Other measures:
- **Completion.** Denominators are fixed: every evaluation counts, completed or not.
- **Objection coverage.**
- **Observation-anchor recall:** whether a report cites evidence at each labeled anchor.
- **Lexical runtime/completeness screen:** produces **review flags for a human, never a score**.
- **Pending judgment:** material-issue detection and actionable usefulness are always reported as `pending-judgment` (C6c-3) and never estimated.

### Governance
- Budgets are declared, not configured. A run refuses to be scored (it requires `--provisional`) while any label is a draft or the thresholds are provisional.
- Label anchors are checked to be **citable**: each one has evidence at that file and line in the captured package. Labels are not derived from analyzer output.
- No LLM judge is used for any metric of record.

## Self-test coverage (36 checks)
- The real corpus validates.
- 11 corpus mutations are rejected: tampered bytes, an unpinned file, a budget different from the host, nonzero tolerance, a foreign label, approval without owner review, an anchor outside the pinned files, an unknown forbidden category, an unknown JSON member, a non-fixture category in C6c-1, and an ID/directory mismatch.
- For each of the 20 fixtures: isolated capture, a unique entry-point match, satisfiable anchors, a clean baseline, and temp cleanup.
- Every integrity-breach class is detected on forged reports. The lexical screen flags without scoring, and fixed denominators are kept.
- The production Gate C path runs on F01 and F10 with model-free roles: completed, 3 checkpoints, zero breaches, full objection coverage.
- The repository corpus is unmodified.

## Owner verification
```powershell
cd C:\Users\dvdht\source\repos\CodeArcheologist
git fetch origin claude/loving-franklin-vq8fco
git switch --detach origin/claude/loving-franklin-vq8fco
.\scripts\check.ps1
```

`check.ps1` now also runs the lab's `--validate-corpus` and `--self-test`.

Optional provisional real-model run (results go outside the repository; with 20 cases × 3 comparators this can take hours on a local GPU, so start with a few cases):

```powershell
dotnet run --project .\tests\evaluation-lab\EvaluationLab.csproj --configuration Release -- --run --out $env:TEMP\ca-lab-provisional --cases F01-attribute-private-helper,F10-prompt-injection-comment --provisional
```
