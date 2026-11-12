# R3 PR3b — Adaptive Receipt Replay and Checkpoint v3, Slice 1

**Approved architecture:** PR3-A through PR3-J. **Prerequisite:** PR3a merged (#63).
**State:** Implementation draft; no owner GREEN has been reported for this slice.

## Scope

This is a **checkpoint/recovery protocol slice**. It deliberately does not expose a new API or launch a partially implemented adaptive agent. Existing Gate C remains the only production execution profile.

- Host maps one of four typed gap categories to one of the same four **read-only** existing evidence capabilities. An agent-provided path, URL, capability name, or node ID cannot authorize a request. Receipt writer rejects gap-kind / capability mismatches and any request after the one follow-up.
- The append-only gap event reader verifies every stored event digest, its role/run/attempt, and its exact ordering.
- `AdaptiveReceiptRevalidator` reopens the **original snapshot** via `IGateCEvidenceSessionFactory.OpenVerified` in a **fresh per-role session**. It checks the adaptive v2 question, scope, ordered requests, host capability mapping, stable E1..En handle issuance, tool-call counters, exact serialized responses, cumulative host-only evidence mappings, item counts, and all follow-up gap events. The resulting receipt-sequence digest binds receipt and gap event IDs and contents, with an explicit role and attempt domain.
- Legacy checkpoint v2 and validator v1 constants are **unchanged**. New, explicitly separate `r3-adaptive-role-checkpoint-v3` and `r3-adaptive-host-validator-v2` versions are legal only for a run with an admitted adaptive v2 context. All acceptance remains fenced. Reviewer requires accepted Investigator/Skeptic checkpoints and may not claim its own evidence receipts.
- `GateC6CheckpointRevalidator` is profile-aware. A v3 accepted role is eligible for reuse **only** if the complete saved receipt sequence reconstructs from freshly verified snapshot evidence and its digest, call count and exact handle mapping agree with the accepted checkpoint. Historical C6b v2 checkpoints continue through their original independent validation path.
- Model-free tests on disposable SQLite and fixture sessions cover successful exact replay, distinct Investigator/Skeptic sessions, mismatched snapshot, failed legacy-v2 admission to an adaptive run, accepted v3 checkpoints and missing gap-event fault injection after dropping an immutability trigger.

## Not implemented in this slice

This is **not yet a functioning adaptive investigation**. The follow-on implementation must add:
- Structured, schema-constrained model gap proposals; denied proposals/diagnostics and deterministic no-gap stopping.
- Host-controlled evidence acquisition and **commit-before-model-delivery** in actual independent MAF role execution.
- Explicit API opt-in and a UI presentation of live gap diagnostics.
- Version-aware StageCoordinator invocation and checkpoint **creation** by the real roles, including explicit continuation of interrupted adaptive runs and the 90-second role, six-call/48-item budgets.
- Fault injection at all remaining commit/restart boundaries and genuine Ollama structured-output verification.

Until the latter is GREEN, **do not admit adaptive investigations through public endpoints or claim any new model capability**. The new v3 contract is used only in model-free tests.

## Verification

```powershell
git fetch origin
git switch feature/r3-adaptive-execution-pr3b
git pull --ff-only
.\scripts\check.ps1
```

The `--r3-adaptive-ledger-self-test` in `check.ps1` exercises the new replay and checkpoint contracts model-free. Historical C6b checkpoint, worker, auth, report, SQL, evaluation and browser tests are still mandatory. No real Ollama is invoked by the new tests. A full GREEN result is required before seeking merge approval.
