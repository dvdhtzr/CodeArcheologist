# R3 PR4 — Preregistered fixed-versus-adaptive comparison

**Owner approval:** PR4 architecture approved 2026-11-12, after merged PR #66.  
**PR4a status:** implementation awaiting owner-local GREEN. No measured adaptive quality result exists.  
**Base:** `main` at `a0d1864c7aa6fd3eedd6996d72866f99ee7d207a` (merged PR3d).  
**PR4b:** separate evaluation execution, blinded judging and verdict; no study conclusion in PR4a.

## Research question and preregistered hypothesis

Does host-governed adaptive evidence retrieval improve the detection of **owner-labelled material code issues** relative to fixed Gate C, on the identical preserved corpus, without lowering operational reliability or evidence integrity?

The immutable experiment record is `eval/r3-pr4-experiment.json` (`r3-pr4-preregistered-comparison-v1`). It pins all 50 `case.json` and approved `labels.json` blobs using canonical Git blob hashes at the base commit, the original C7 lab configuration blob, comparator order, sample, repetitions, metrics and acceptance conditions. The lab verifies those bytes **without running Git**, before any experimental model call. Under `.gitattributes`, `eval/** -text` also prevents Windows checkout from transforming the pinned bytes. These Git hashes are provenance pins, not MACs or semantic evidence.

The prior C7 `eval/lab-config.json`, case inputs, signed labels, official scored run, ADR 0020/0022, host budgets, model, prompts, checkpoints, and original command semantics are **unchanged**.

### Population, arms and handling of missing runs

- **50 pinned cases:** 20 purpose-built fixtures, 15 one-edit change experiments and 15 pinned public repositories. Of these, 46 admit investigations; R10, R12, R14 and R15 are negative expectations and must remain confirmed, not quietly disappear.
- **Four arms:** `deterministic-baseline`, `gate-b`, `gate-c` (fixed), `gate-c-adaptive`. The last two use the same fixed host-owned question semantics and actual production worker, .NET provider and local `qwen3.5:9b`. They do not get to modify the question or evidence capability catalog.
- **Trials:** one primary trial per eligible case plus two additional diagnostic trials for each of the same 12 preselected diagnostics in *both* Gate C arms. Thus 46 primary comparisons and 70 trials per Gate C arm. No trial may be dropped for timeout, bad JSON, rejection, missing repo or failed verdict. Report the actual denominator and failure separately.
- **Fairness:** same snapshots, task labels, model IDs, 90-second role and 360-second attempt deadlines, six available tool slots and 48 unique items/role. Fixed may select up to three initial views; adaptive may select up to three plus one host-governed follow-up (maximum four). That asymmetry is the experimental treatment, not a hidden matched-resource assumption. Workloads are run sequentially under independent fenced runs. No external LLM or network is used by the application.
- **Source privacy:** public pinned repositories and locally authored fixtures only. Curated checkouts are owner-acquired outside the application. The lab never invokes a build, imported code or Git on these repositories.

## Exact metrics and gates (fixed before scored results)

The primary outcome is the **paired material-issue detection difference** between adaptive and fixed Gate C, in percentage points, on owner-labelled issues across the 46 eligible primary-case reports, with missing/incomplete reports contributing zero detected issues. Labels and thresholds must not be changed to rescue a failed experiment.

A successful quality claim requires *both* an adaptive gain of at least **10 percentage points** *and* a **95% case-clustered paired-bootstrap confidence interval strictly above zero**. A confidence interval including zero does not warrant a positive improvement claim, regardless of the observed point estimate. The same owner-labelled issue set is shown to an external blinded adjudicator in PR4b, and all question/answer records stay distinct from the C7 record. The judge cannot rewrite metrics, labels or incomplete-run status.

Operational requirements: zero integrity breaches, 100% material objection coverage among eligible accepted reconciliations, adaptive workflow completion at least 90%, and an adaptive completion rate no more than **5 percentage points below** fixed Gate C. Actionable usefulness must not regress on blinded primary-case judgments; the exact nonregression rule and its denominator will be finalized in PR4b **before** results are viewed. Observation-anchor recall, elapsed wall time, evidence requests/unique items, durable follow-ups/denials, model/role failures, and compute cost are descriptive secondary measures, never substitutes for usefulness.

**Important:** These are separate R3 PR4 conditions, **not** retrospective changes to C7's 85% detection / 80% actionability / 15-point Gate B gain thresholds. C7 judgment still remains pending.

## PR4a implementation boundary

`tests/evaluation-lab` adds a separate `--r3-pr4-validate` and `--r3-pr4-run` command. The existing `--run`, `--self-test`, `--validate-corpus`, C7 schema and official comparators are unchanged. PR4 mode alone adds the fourth comparator and writes its own `r3-pr4-lab-summary-v1` result.

Adaptive trials call `InvestigationAdmissionService.AdmitAdaptive` with the exact saved candidate kind and run the **real** `InvestigationWorker`, `GateC6StageCoordinator`, `AdaptiveMafRoleStageRunner` and Reviewer. They do not manufacture reports. The host owns evidence receipts and checkpoint v3 acceptance. Every PR4 Gate C trial independently revalidates checkpoints. Each adaptive trial additionally uses the same `AdaptiveActivityProjectionService` as the API, which independently replays all receipts and gaps from the saved snapshot and fails closed on damaged provenance.

The separate `r3-pr4-host-activity-v1` sidecar records persisted host requests and receipts for adaptive trials, and verified accepted-checkpoint counts for fixed trials. Those are **not perfectly equivalent observational surfaces**: incomplete fixed-role request counts are unknown, not zero. Model-call counts are intentionally `null` in PR4a, not guesses. PR4b must add real provider-facing instrumentation before claiming model-call or cost comparisons.

`pr4-run-metadata.json` records the protocol's SHA-256, comparator order, selected cases and provisional/scored status. A PR4 run refuses to overwrite an existing nonempty output directory. A non-provisional run requires every locked case, all four comparators and `--curated-root`. A provisional local debug run may select a subset, but it is **never** evidence of improvement. Incomplete or corrupted host history must not be silently promoted to success; a crash invalidates the experiment until a fresh complete output is obtained.

Model-free self-tests (in `--self-test`) reject changed approved-label bytes, case bytes, C7 config bytes, comparator additions, threshold edits, and missing case locks; they also execute an adaptive admitted production worker with deterministic independent roles, real fenced receipt writes, independently accepted/replayed v3 checkpoints, an existing model-free Reviewer, and the adaptive integrity scorer. Existing PR3c/d fault suites continue to cover write-before-delivery and tampered receipt/gap failures.

## Owner verification / no merge before GREEN

Run on Windows from the PR4a feature branch:

```powershell
git fetch origin
git switch feature/r3-pr4a-comparative-lab
git pull --ff-only
.\scripts\check.ps1
dotnet run --project .\tests\evaluation-lab\EvaluationLab.csproj --configuration Release -- --r3-pr4-validate
```

The real adaptive Ollama smoke test remains separate:

```powershell
dotnet run --project .\tests\maf-adapter-contract\MafAdapterContractProbe.csproj --configuration Release -- --r3-adaptive-run 1
```

Optional **provisional** single-case integrated comparison, only after model-free GREEN:

```powershell
dotnet run --project .\tests\evaluation-lab\EvaluationLab.csproj --configuration Release -- --r3-pr4-run --out "$env:TEMP\ca-r3-pr4-smoke-$(Get-Date -Format yyyyMMddHHmmss)" --cases F01-attribute-private-helper --comparators gate-c,gate-c-adaptive --provisional
```

PR4b will establish the final frozen blinded question packets, exact judgment denominators, provider-facing resource counters, and calculate the comparative verdict. No scored full-corpus quality claim should be made until that work passes its own GREEN gate. **Do not merge PR4a without owner-local full GREEN and explicit approval.**
