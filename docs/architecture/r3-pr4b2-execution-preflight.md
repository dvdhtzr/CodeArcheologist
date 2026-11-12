# R3 PR4b2 — Phase A execution preflight (NO scored study)

**Status: draft; owner-local GREEN is pending.** Branch: `feature/r3-pr4b2-execution-preflight`, from merged PR #68. PR4b2 authorization was **preflight-first**. This document does not authorize Phase B, the 232-trial scored study, judgments, or a verdict.

## Why this change exists

PR4b1 sealed all 232 expected report/score slots, including 140 role-activity sidecars and 19 negative/change expectations, but did not prove the real execution environment or a live cross-comparator call count. A tag such as `qwen3.5:9b` is not an immutable model identity. PR4b2 Phase A verifies readiness without changing the preregistered experiment, labels, questions, MAF prompts, evidence capabilities, worker budgets, or any production database format.

### New preflight safeguards

1. **Observed provenance on all PR4 runs:** `pr4-run-metadata.json` records the local Ollama tag, exact digest from `/api/tags`, Ollama `/api/version`, trusted Code Archeologist `git rev-parse HEAD`, .NET/OS/architecture, and MVIDs for both the executing evaluation-lab and API assemblies. The local HTTP probes are read-only and do **not** invoke the model. Scored runs require a clean trusted Code Archeologist checkout. At the end of a scored study the same identity is observed again; any change or failed probe leaves the study **unsealed**. These observations are SHA-256-bound into the existing run seal by PR4b1's unchanged artifact-sealing process. This is not a cryptographic identity attestation or evidence that the digest remained constant at every instant between probes.
2. **Audit validation:** a scored census with missing, malformed or tag-incompatible identity fails closed. The synthetic model-free census tests reject removed identity metadata and preserve the exact sealed bytes.
3. **Incomplete-trial regression:** a synthetic 232-slot run includes a properly recorded provider-failed primary trial. It is accepted as a complete **census**, not as a successful investigation; removing that failed trial's activity makes the census invalid. The existing C6b/R3 fault suites independently test actual host stage cancellation, receipt ordering, checkpoint revalidation, failures before evidence delivery, and SQLite fencing.
4. **Curated/source readiness:** the owner-run PowerShell preflight independently checks 15 clean checkouts at pinned commits; pinned licence bytes and subtree paths; and read-only snapshot fingerprints for the 14 non-capture-limit checkouts. The R15 oversized checkout is tested as the explicit fail-closed capture-limit expectation together with R10/R12/R14 negative cases. The evaluator never builds or executes any curated source.
5. **Real-provider acceptance:** full `check.ps1`, `--r3-adaptive-run 1`, and a provisional single-case fixed/adaptive comparison must all pass. Both completed Gate C arms must show three validated roles, three checkpoints, accepted replay and meaningful provider-facing attempt counts (at least 5 for fixed; at least 7 for adaptive, given the unchanged initial selection, assessment and Reviewer calls). Fault count must be zero in this success smoke. These counts exclude internal Ollama retries and tokens.
6. **Separation of studies:** the preflight deliberately asks the PR4 auditor to reject the provisional negative-case run and provisional paired smoke. The preflight does not seal or export judgments, run all 232 trials, or decide any comparative outcome.

## Owner-local commands (Windows, from this repository only)

Stop any Code Archeologist dev server using port 5127 before starting. Verify and check out the draft feature branch:

```powershell
cd C:\Users\dvdht\source\repos\CodeArcheologist
git fetch origin
git switch feature/r3-pr4b2-execution-preflight
git pull --ff-only
git status --short
```

The curated checkout directory must be **outside** the Code Archeologist repository. If the 15 repositories have not been acquired yet, run the existing trusted acquisition script; it downloads exact commits, verifies licences and never builds or runs sources:

```powershell
$curated = Join-Path $env:TEMP 'ca-pr4-curated'
.\scripts\acquire-curated.ps1 -Target $curated
```

Then perform the entire Phase A preflight in one command:

```powershell
.\scripts\r3-pr4b2-preflight.ps1 -CuratedRoot $curated
```

The script writes an independent `preflight-record.json`, a provisional fixed/adaptive smoke directory, and a provisional negative-case directory under a new, timestamped output location **outside the repository**. It aborts on any mismatch and never reports GREEN if a required step failed. Keep the output, especially if RED, and share the first failure plus its preserved evidence for review. If `$curated` points somewhere else on your machine, set it to the actual existing checkout root; do not reacquire unnecessarily.

## Recovering a prior passing preflight after final-record-only failure

If the prior run visibly reached `VERIFIED: provisional paired smoke cannot pass the scored census.` and **then** failed to write the final provenance record, reuse its existing output rather than repeating the expensive local/model checks. This is a separate **owner-attested recovery**, not an independently rerun full preflight. The recovery validates the two trial artifacts and counters, 4 negative checks, 15 repository commits/licences, frozen protocol hash and exact Ollama identity; it also checks that executable source has not changed since the original smoke (other than these preflight scripts). The previous 14 fingerprints, full `check.ps1` and separate adaptive acceptance are expressly recorded as owner-confirmed, not rerun.

```powershell
cd C:\Users\dvdht\source\repos\CodeArcheologist
git pull --ff-only
$curated = Join-Path $env:TEMP 'ca-pr4-curated'
$previous = Get-ChildItem $env:TEMP -Directory -Filter 'ca-r3-pr4b2-preflight-*' |
    Where-Object { (Test-Path (Join-Path $_.FullName 'provisional-smoke/pr4-run-metadata.json')) -and
                   -not (Test-Path (Join-Path $_.FullName 'preflight-record.json')) } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if ($null -eq $previous) { throw 'No previous finalization-only failed run found.' }
.\scripts\r3-pr4b2-finalize-existing.ps1 -Out $previous.FullName -CuratedRoot $curated -ConfirmPriorGatesPassed
```

Review that the selected output folder is your failed run before invoking. No source is compiled or executed by the recovery script; no full scored study or model requests occur. If source beyond the preflight scripts changed since the earlier run or any artifact/identity check fails, repeat the normal full preflight on a fresh output directory instead.

## Acceptance criteria

- All 50 frozen case/label blobs, 15 curated repository commits, licences, subtrees and applicable fingerprints match exactly; all four negative expectations including capture-limit fail closed.
- Full `.\scripts\check.ps1` is GREEN, including C6b and R3 fault-injection suites, the PR4b1 synthetic seals and the additional PR4b2 failed-slot/provenance regressions.
- Real `qwen3.5:9b` adaptive MAF probe and both real Gate C provisional arms are GREEN; both arm sidecars demonstrate counts, checkpoint revalidation and adaptive receipt replay.
- Run metadata contains exact observed Git/model/runtime identity, and the PR4 census rejects its absence. Scored study requires a clean code checkout and checks observed identity again before sealing.
- Provisional or incomplete studies never pass the scored audit. No labelled data, threshold, model prompts or comparator protocol is changed.
- **Owner explicitly reports local GREEN, then separately approves any merge.** Even after merge, the 232-trial Phase B study needs **separate explicit authorization**.

## Limitations and failure interpretation

The scripted successful Ollama smoke proves counters are nonzero under the *real managed worker*; synthetic fault cases prove failed artifacts are not silently dropped. The preexisting R3 fault suite covers injected persistence/cancellation failures, but this preflight does not intentionally break the live Ollama service or guarantee every conceivable transport timeout is reproduced. An actual provider failure during a later study must occupy a slot if the worker returns a durable incomplete report; a host crash that leaves artifacts missing must invalidate the **study**. A real reported incomplete trial must never be overwritten or selectively rerun to make results look favorable.

Provisional flags and the observed preflight identity are not an authorization token for the scored run. The user remains the authority for Phase B. The SHA-256 run seal is a corruption detector, not an external signature. Fixed/adaptive workflows differ in prompts and interaction sequence; positive findings would concern the workflows **as implemented**, not an isolated retrieval intervention.
