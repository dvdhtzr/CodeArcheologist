# Gate C6c — Hybrid evaluation laboratory

Status: **C6c-1: owner reported `check.ps1` GREEN on Windows; Linux passed in the authoring container.** The decisions are approved (ADR 0020). No acceptance result exists yet. All 20 labels are assistant drafts awaiting owner sign-off, and the thresholds are still provisional.

## Slices

| Slice | Scope | State |
|---|---|---|
| C6c-1 | Case and label schema, corpus validator, budget declaration, isolated harness, 3 comparators, deterministic metrics, 20 fixtures | owner GREEN (Windows `check.ps1`); Linux passed; label sign-off pending |
| C6c-2a | 15 curated real repositories: manifest, owner-run acquisition scripts, subtree scoping, expectation kinds, BOM analyzer fix | implemented; Linux self-test and provisional baseline run passed; owner verification and label sign-off pending |
| C6c-2b | 15 deliberate change experiments | not started |
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

## Curated repositories (C6c-2a)

`eval/curated/repositories.json` (schema `c6c-curated-v1`) pins, for each repository: the GitHub URL, the **full commit SHA**, the licence (MIT, Apache-2.0 or BSD-3-Clause only), the licence file and its SHA-256, the **subtree**, the expected **snapshot fingerprint** with its file count, the group, and the purpose.

| Group | Cases | Expectation |
|---|---|---|
| attribute-controllers | R02 RealWorld, R03 modular-monolith (`src/API`), R04 Equinox | investigate |
| minimal-api | R01 eShopOnWeb (`src/PublicApi`), R05 TodoApi, R06 jasontaylordev, R07 eShop (`src/Ordering.API`), R08 dotnet-podcasts, R09 aspire-samples, R11 aspnetrun (Carter), R13 HealthChecks.UI | investigate |
| unsupported-convention | R10 ardalis (`src`, FastEndpoints), R12 Ocelot (`samples/Basic`, `ocelot.json` routing), R14 Contoso (Razor Pages) | no-supported-candidate |
| capture-limit | R15 OrchardCore | capture-limit-fail-closed |

- **Acquisition is owner-run and outside the app:** `scripts/acquire-curated.ps1 -Target <dir>` (or `.sh`).
  - Each repository is fetched at its exact pinned SHA, with `core.autocrlf=false`, `core.eol=lf`, and LFS smudge skipped, then the licence hash is verified.
  - The script refuses a target inside the repository, and it never builds or executes anything.
  - The application and the lab never invoke Git.
- **The lab captures the checkout in place, read-only.** Any of the following fails closed before any comparator runs: a missing checkout ("not acquired"), a licence mismatch, a subtree mismatch, or a fingerprint mismatch.
- **Cases are never silently dropped.** Curated cases without `--curated-root` are listed as `notAcquired` in the summary.
- **Survey and pin results** (Linux container, reproduced from the script's own fetch-by-SHA checkouts): all 11 investigate cases resolve their entry points with zero baseline integrity breaches. R10, R12 and R14 report zero candidates, and R15 fails closed (413) with no snapshot record.

## Self-test coverage (49 checks)
- **Curated machinery (12 checks, no network):** a synthetic checkout is pinned in a temp corpus copy, captured, then rejected when its licence is tampered or its bytes are CRLF-converted. Also covered: a missing root, both expectation kinds (confirmed and violated), and 7 manifest mutations (abbreviated SHA, non-permissive licence, traversal subtree, unpinned fingerprint, URL mismatch, group/expectation mismatch, a repository without a case).
- **BOM regression (1 check).
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

Curated acquisition and provisional curated run (network required only for acquisition):

```powershell
.\scripts\acquire-curated.ps1 -Target $env:USERPROFILE\CodeArcheologistCurated
dotnet run --project .\tests\evaluation-lab\EvaluationLab.csproj --configuration Release -- --run --out $env:TEMP\ca-lab-curated --curated-root $env:USERPROFILE\CodeArcheologistCurated --comparators deterministic-baseline --provisional
```

Optional provisional real-model run (results go outside the repository; with 20 cases × 3 comparators this can take hours on a local GPU, so start with a few cases):

```powershell
dotnet run --project .\tests\evaluation-lab\EvaluationLab.csproj --configuration Release -- --run --out $env:TEMP\ca-lab-provisional --cases F01-attribute-private-helper,F10-prompt-injection-comment --provisional
```
