# R3 PR4b1 — sealed census, passive model-call accounting, blinded judging and frozen verdict

**Approval:** owner approved PR4b1 design on 2026-11-12. **PR4b1 only**; no production study execution or comparative claim.  
**Base:** merged PR #67, `8fe6017d4bbb2381b4f43366782eaa3149e07265`.  
**Status:** draft, local verification required. Do not merge until owner reports GREEN and approves.

## Immutable prerequisite

`eval/r3-pr4-experiment.json`, the 50 owner-approved labels and cases, historical `eval/lab-config.json`, C7 judging data, model and host budgets **are not modified**. PR4b1 hard-codes/finalizes the interpretation of the already preregistered metrics before any scored PR4 runs are evaluated:

- **46 primary paired cases**, trial 1 only: every labelled material issue is judged once for fixed Gate C and once for adaptive Gate C. Incomplete reported results count as "not detected".
- **70 fixed and 70 adaptive Gate C attempts** for completion and host cost: 46 primary + two repeats for 12 predeclared diagnostic cases. Extra diagnostic trials do not increase primary sample size.
- **4 negative expectation cases** and **15 change-experiment expectations**, required confirmed. Thus there are **232 report/score slots** (46 baseline, 46 Gate B, 70 fixed Gate C, 70 adaptive Gate C) and **140** attempted Gate C host-activity sidecars.
- **Gates:** zero integrity breaches; accepted adaptive objection coverage 100%; adaptive completion ≥90%; completion no more than 5 points below fixed; detection improvement ≥10 points with 95% paired case-cluster bootstrap interval strictly above zero; adaptive actionable report count **no less than fixed** on the 46 primary cases. All failure gates are reported, not retrospectively relaxed.
- **Judging denominator:** every labelled issue in the 46 primary cases; every case gets one blinded A/B usefulness question, even if an arm failed. Missing/incomplete reports are displayed honestly and are not actionable.
- **Paired bootstrap:** seed `20261112`, 10,000 resamples, sample 46 case clusters with replacement, preserving all issues within each selected case, then compute the issue-weighted detection-rate difference in percentage points. A rare draw with zero labelled issues has undefined detection; redraw it deterministically until an issue-bearing draw is obtained or fail after 1,000 tries. Percentile endpoints use the frozen floor(0.025×9999) and ceil(0.975×9999) sorted ranks. An interval touching zero **cannot** establish improvement.
- **Uncalibrated judgment:** no guessed earlier owner A/B choices are used as calibration. The single external judge is disclosed as uncalibrated, partly blinded because report styles can reveal the arms. This limits claims; it does not alter the numeric thresholds.

## PR4b1 boundaries and protections

`R3Pr4Census` validates the exact expected artifacts, all metadata and comparator slots, report/score congruence, all negative/change expectations, unique run IDs and attempt IDs, and both Gate C role-activity versions. Missing artifacts fail the **entire study**, whereas a present incomplete model report remains a valid failed trial. A non-provisional `--r3-pr4-run` refuses success until this census passes; it creates `pr4-complete-seal.json` only after a complete, valid result. `--r3-pr4-audit` rechecks hashes on every artifact and the frozen PR4 protocol. Provisional subsets are never sealed or accepted as scored results.

The seal is SHA-256 over captured files and the original protocol digest. It detects accidental changes and post-run corruption; **it is not a digital signature, independent trust anchor or proof against a privileged writer**. Role claims are source-only and evidence checkpoints are independently replayed by the real managed worker during each scored trial. A later offline audit does **not** rerun the original source snapshots.

`EvaluationModelCalls` is an opt-in, scoped counter inherited through the managed worker's async execution context. It wraps the actual Gate C Investigator/Skeptic and Reviewer MAF invocation boundaries, including adaptive role prompts. Captures attempted calls and thrown provider faults, not inferred token counts, provider-internal retries or model cost. Without an active PR4 lab observer the counter is inert. Host evidence receipts are still the sole authority for adaptive evidence; no prompts, roles, host budgets, checkpoint formats or production model behavior are modified.

**Separate PR4 judgment namespace:** `--r3-pr4-export` creates deterministic randomized, 12-question text packets and a **private answer key**. Each packet's exact bytes are hashed in the key. Upload only `upload-these-to-chatgpt/*.txt`; never share the key. `--r3-pr4-import` requires one correctly typed complete answer per question, rejects false claims about missing reports and changed packets, and writes the *first complete* PR4 judgment file with `FileMode.CreateNew`. The PR4 verdict independently binds the full sealed run to the imported key and answers and computes the fixed paired-bootstrap result. C7's `--export-judging`, `--import-judging` and `--verdict` remain unaffected.

No model or external API is called by exporter, importer, auditor or verdict. The PR4b2 execution stage will be separately reviewed and must use this already frozen pipeline; do not run or judge the scored 50-case study under PR4b1.

## Owner-local verification — Code Archeologist only

```powershell
cd C:\Users\dvdht\source\repos\CodeArcheologist
git fetch origin
git switch feature/r3-pr4b1-evaluation-integrity
git pull --ff-only
.\scripts\check.ps1
dotnet run --project .\tests\evaluation-lab\EvaluationLab.csproj --configuration Release -- --r3-pr4-validate
```

The full `check.ps1` invokes the lab self-test suite: it now includes synthetic complete 232-slot output, exact sealed artifacts, altered/missing/extra-file rejection, blinded packet alteration, incomplete/duplicate import rejection, first-record immutability, paired-bootstrap positive and no-effect fixtures, and real managed-worker telemetry flow through a deterministic fixture. **These are model-free structural tests; fabricated synthetic outputs are not semantic evidence.**

After model-free GREEN, the owner may optionally repeat the separate PR3d genuine adaptive MAF acceptance:

```powershell
cd C:\Users\dvdht\source\repos\CodeArcheologist
dotnet run --project .\tests\maf-adapter-contract\MafAdapterContractProbe.csproj --configuration Release -- --r3-adaptive-run 1
```

Only **PR4b2**, after owner approval, will run the full genuine scored 50-case evaluation, export and adjudicate packets, calculate the real verdict, and investigate limitations or failures without changing frozen criteria.
