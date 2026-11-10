# Gate C6b — Managed worker and explicit recovery

Status: **in progress on a draft PR.** Architecture approved by the owner (decisions D1–D5). Phases 1–4: owner reported GREEN on Windows (`check.ps1`, `--gate-c4-run 3`, `--gate-c6b-run 2`, manual restart/continuation walkthrough). Linux `check.sh` passed in the authoring container. **macOS verification is pending; cross-platform locking is not claimed until it passes.**

## Approved decisions (summary)

| | Decision |
|---|---|
| D1 | One managed .NET hosted worker (`BackgroundService`) owns execution. No detached `Task.Run`. |
| D2 | Manual recovery only. A restart never resumes work or calls Ollama by itself. |
| D3 | Hybrid evaluation laboratory. **Out of scope for C6b.** |
| D4 | OS process lock + SQLite fencing epochs. A heartbeat never grants ownership. |
| D5 | Invalid checkpoints fail closed (Phase 3). |

## Phase status

| Phase | Scope | State |
|---|---|---|
| 1 | SQLite v6, OS lock, fenced ownership, startup reconciliation, ownership tests | owner reported GREEN |
| 2 | Managed worker replaces `Task.Run`; single terminal write; shutdown/deadline semantics | owner reported GREEN |
| 3 | Role checkpoints, evidence-handle reconstruction, compatibility checks, `POST .../continue` | owner reported GREEN (including `--gate-c6b-run`) |
| 4 | Cancellation intent, `GET .../execution`, `POST .../cancel`, minimal UI | owner reported GREEN (Windows); Linux passed in the authoring container; macOS pending |

## Responsibilities

1. **Admission** — `InvestigationAdmissionService`. The endpoint handles authentication, CSRF, scope checks and the idempotency key. The service does a fenced, idempotent, transactional admission and then sends the worker a wake-up signal. It never runs MAF. Only the current fenced owner can admit work. Any other process answers `503`.
2. **Managed worker** — `InvestigationWorker` (hosted service). It handles claiming, the per-attempt deadline (6 minutes by default), the heartbeat, shutdown and the single terminal write.
3. **Checkpoint-aware coordinator** — `GateC6StageCoordinator` (Phase 3; see below). Role timeouts (90 s) and the bounded retry rules are unchanged.
4. **Persistence** — `WorkspaceStore` partials. Every mutation of an active attempt checks the fence inside its own transaction.

## OS execution lock

- File: `<data dir>/execution.lock`, opened with `FileShare.None` and held for the life of the process.
  - Windows: an exclusive share mode.
  - Linux and macOS: .NET applies `flock(LOCK_EX | LOCK_NB)`.
- **Self-probe:** after acquiring the lock, the process tries a second exclusive open of the same path. If that open succeeds, the platform is not enforcing the lock, and ownership is refused with `os-file-locking-not-enforced`.
- If `System.IO.DisableFileLocking` or `DOTNET_SYSTEM_IO_DISABLEFILELOCKING` is set, ownership is refused with `os-file-locking-disabled`.
- The file's contents (owner, epoch, PID) are diagnostic only. A lock file that merely exists is never authority.
- Network filesystems are unsupported. The supported deployment is application instances on one machine sharing one local data directory.
- Cross-platform status: verified on Linux in the authoring container only. Windows and macOS remain unverified until the owner runs the checks there.

## Fencing

- The `execution_owner` singleton row holds a random `owner_id`, a monotonic `ownership_epoch`, acquisition and heartbeat times, the PID (diagnostic only), a status and a diagnostic.
- Triggers enforce that the epoch only ever advances by exactly 1, that a new owner requires a new epoch, and that the row can never be deleted. `execution_ownership_history` is append-only.
- `EstablishOwnership` takes the held `ExecutionProcessLock` object. There is no API that obtains ownership from heartbeat age.
- Fenced operations — each fails with `ExecutionFenceException` and rolls back:
  - admission
  - claim (queued → running, which also sets the deadline)
  - stage events
  - report completion
  - failure
  - interruption
  - heartbeat
  - owner release
- The fence is checked before any state is read. The attempt must also carry the same owner and epoch.
- Slot release happens only inside a fenced terminal transaction, and only for the slot's own attempt.

## Startup protocol (`Program.cs`)

1. `WorkspaceStore.Initialize()` migrates the schema only. Since C6b it never recovers work. Probes and second processes may call it safely.
2. `ExecutionOwnershipState.TryAcquire` tries the OS lock without blocking.
3. If the lock is unavailable: no execution, no reconciliation, no epoch change.
4. If the lock is acquired: establish a new epoch.
5. `ReconcileAbandoned(fence)` finalizes earlier owners' work:
   - C6b `queued`/`running` attempts → `interrupted` (cause `process-interrupted`), and the run → `interrupted`.
   - C6a v1 attempts keep their legacy non-resumable semantics (cause `legacy-c6a-restart`, report `incomplete`).
   - Pre-C6 running reports → `incomplete`, as before.
   - The slot is released.
6. Accepted checkpoints are never touched.
7. The worker idles. It claims only attempts admitted under its own epoch, so it never launches a model call at startup.

## Worker terminal semantics

- The executor call is always awaited to completion. Cancellation or a deadline only signals the token. If the call is uncooperative, the slot stays held and new admissions get `busy` — fail closed rather than run two model calls at once.
- Classification comes from host-observed tokens first, then the outcome:

  | Observation | Attempt | Run | Cause |
  |---|---|---|---|
  | Ownership lost | no write; worker halts | — | — |
  | Application shutdown | `interrupted` | `interrupted` | `application-shutdown` |
  | Attempt deadline | `interrupted` | `interrupted` | `attempt-deadline` |
  | Executor fault | `failed` | `incomplete` | `host-failure` |
  | Report factory rejected | `rejected` | `incomplete` | `report-validation-failed` |
  | Completed | `completed` | `completed` | `completed` |
  | Role timeout | `incomplete` | `interrupted` (recoverable) | `role-timeout` |
  | Provider/adapter error | `failed` | `interrupted` (recoverable) | `provider-error` |
  | Checkpoint revalidation failed at execution time | `rejected` | `interrupted` (continuation stays fail-closed) | `checkpoint-rejected` |
  | Stage/checkpoint write failed | no write; worker halts (crash before commit) | — | — |
  | Validation rejected | `incomplete` | `incomplete` (terminal) | `validation-rejected` |
  | Evidence rejected | `rejected` | `rejected` | `evidence-rejected` |

  Phase 3 change from Phase 2: role timeouts and provider errors no longer write a terminal C5 report. They leave the run `interrupted`, so accepted checkpoints can be reused by an explicit continuation. A host validation rejection stays terminal: the system never automatically or implicitly redoes a role whose output the host rejected.

- **Exactly one terminal write is attempted.** If it fails, the worker halts and keeps the slot. It never tries a second, different finalization, so no incorrect completed state can appear. The next explicit restart reconciles the attempt as `interrupted`.
- **Hazard that fencing cannot address:** if this process dies, the local Ollama server may keep running an inference that was already sent. Fencing stops database writes, not GPU work. Because recovery is manual only, nothing new is launched until the user explicitly acts.

## SQLite v6 (forward-only; one transaction)

The table rebuilds follow SQLite's documented procedure: foreign keys are suspended outside the transaction, and `foreign_key_check` must be clean before commit.

- `investigation_reports` is rebuilt to add a run-level `interrupted` status, with `report_json` required to be NULL in that state. All rows are copied unchanged.
- `investigation_attempts` gains `continues_attempt_id`, `terminal_cause` (CHECK-enumerated), `deadline_at_unix_ms` and `cancel_requested_at_unix_ms`. New attempts use `gate-c6-execution-attempt-v2`.
- `investigation_events` is rebuilt with the expanded taxonomy, a `stage` CHECK and `ownership_epoch`. Historical rows are copied unchanged (epoch NULL, event contract v1). The append-only triggers are recreated. New events use `gate-c6-investigation-event-v2`.
- `investigation_checkpoints` is recreated as run-scoped (`UNIQUE(run_id, role)`) with full binding columns: scope, fingerprint, entry point, evidence, capability, analyzer, model, validator and assessment versions, links, validation, dependencies, digest and acceptance epoch. It is immutable. Migration **refuses** if v5 contains any checkpoint row, because such a row has unknown provenance.
- New tables:
  - `attempt_checkpoint_lineage` (append-only; `accepted` / `reused`)
  - `investigation_execution_requests` (continue/cancel idempotency)
  - `execution_owner`
  - `execution_ownership_history`

**Caution while the PR is open:** v6 may still change before merge. Migration is forward-only, so verify with disposable data directories (the self-tests do this), not with your real `%LOCALAPPDATA%\CodeArcheologist`.

## Phase 3 — checkpoints and explicit continuation

### Coordinator (`GateC6StageCoordinator`)
- Revalidates any accepted checkpoints at execution time, even after the continuation request already did. If revalidation fails it records `checkpoint-validation-rejected` and ends the attempt with `checkpoint-rejected`.
- Runs Investigator → Skeptic → Reviewer through the unchanged `GateCStateTransitions` and executes **only unfinished stages**.
- Before writing a checkpoint, the issuing role session re-checks the role output: the assessment is revalidated and the evidence links must match exactly. A Reviewer output is re-checked with `ValidateReview`.
- `IGateCRoleStageRunner.RunIndependentAsync` receives only the role's own fresh session; no parameter exists through which another role's output could flow. In production, `MafRoleStageRunner` calls the existing genuine MAF role functions, so model semantics are unchanged.
- **Skeptic independence after continuation:** a continued Skeptic opens a new verified session (no handles, no tool calls) and never receives the Investigator checkpoint.

### Checkpoint acceptance (`WorkspaceStore.AcceptCheckpoint`, fenced)
- One transaction inserts the checkpoint, the `accepted` lineage row and the `checkpoint-accepted` event (which carries the checkpoint ID).
- One checkpoint per role per run.
- Independent roles may not depend on other checkpoints. The Reviewer must depend on exactly the Investigator and Skeptic checkpoints.
- The canonical SHA-256 covers every bound field: IDs, role, scope, fingerprint, entry point, evidence/capability/analyzer/model/validator/assessment versions, assessment, role-local evidence links, validation, dependencies, acceptance time and epoch.
- Reuse is recorded as a `reused` lineage row plus a `checkpoint-reused` event in the new attempt. The original checkpoint row and its provenance are never rewritten.

### Revalidation (`GateC6CheckpointRevalidator`) — fails closed under a strict compatibility policy

| Reject reason | Condition |
|---|---|
| `snapshot-changed-or-foreign` / `snapshot-changed-or-invalid` | The snapshot row differs, or the saved bytes fail re-verification |
| `checkpoint-missing-or-unrecorded` | The immutable `checkpoint-accepted` events and the checkpoint rows disagree |
| `checkpoint-digest-mismatch` | The recomputed digest does not match |
| `checkpoint-foreign-scope` | Wrong run or scope |
| `checkpoint-contract-incompatible` | Checkpoint, evidence, capability or assessment contract version changed |
| `checkpoint-validator-incompatible` | Validator version changed |
| `checkpoint-model-provenance-incompatible` | Model ID differs from the current one |
| `checkpoint-analyzer-incompatible` | The freshly verified session's graph analyzer version differs |
| `foreign-or-altered-evidence-mapping` | `GateCEvidenceSession.RestoreHandles` failed: each stored role-local link must name exactly one canonical item of the same status in the re-verified package, follow the session's own `E1..En` order, and reproduce identical report links. Handles are never remapped |
| `checkpoint-assessment-revalidation-failed` | The C1 host validator rejected the assessment against the restored catalog |
| `checkpoint-review-revalidation-failed` | `ValidateReview` rejected the Reviewer checkpoint |
| `checkpoint-lineage-invalid` / `checkpoint-stage-dependency-invalid` | Lineage or stage dependency is invalid |

### `POST .../investigations/{runId}/continue`
- The request is authenticated and CSRF-guarded, and requires an `Idempotency-Key` and a body `{ "expectedAttemptNumber": n }` (optimistic concurrency).
- Checks, in order:
  1. Scope lookup.
  2. Exact idempotent replay, which returns the original attempt. A reused key with a different request conflicts.
  3. This process must be the lock owner (otherwise `503`).
  4. Already completed (`409`), legacy run (`409`), stale expected attempt (`409`), another active execution (`409 busy`), run not interrupted (`409`).
  5. Snapshot and full checkpoint revalidation (`422` with a safe diagnostic; **no attempt is created**).
- Then one fenced transaction re-checks idempotency, state, the unchanged checkpoint set and the free slot. It creates attempt N+1 under the same `run_id` with `continues_attempt_id`, sets the run back to `running`, reserves the slot and appends `continuation-admitted`. The worker is signalled; the request itself never runs a model.
- A fresh investigation remains the separate, existing create endpoint.

### Deterministic coverage (in `--gate-c6b-self-test`)
- All three roles are checkpointed after host acceptance, with exact event chronology and immutability.
- A forged handle is rejected with no checkpoint.
- **Crash before the Skeptic checkpoint commits** (fault-injected): no Skeptic checkpoint and the Investigator checkpoint survives. After restart (no model call) and explicit continuation:
  - the Investigator is not re-run;
  - a fresh, empty Skeptic session runs, then the Reviewer;
  - the lineage and new-attempt events are correct and the historical attempt is unchanged;
  - replay is idempotent and a reused key with a different request conflicts.
- Role timeout leaves the run recoverable, and continuation completes it.
- **Three valid checkpoints with a crash before the terminal report:** continuation completes the report with zero model calls.
- Stale, busy, cross-scope and non-owner continuation guards. A Reviewer checkpoint without a Skeptic checkpoint is refused, and an independent role with a dependency is refused.
- **Nine tamper cases fail closed** with the exact diagnostic, no new attempt, the run unchanged and no role re-run: digest, foreign scope, altered mapping, validator, evidence contract, model, analyzer, changed snapshot, missing checkpoint.

### Genuine MAF/Ollama walkthrough — `--gate-c6b-run <trials 1..3>`
- Runs the production worker, coordinator and `MafRoleStageRunner` on a disposable, never-compiled synthetic fixture in an isolated data directory.
- Each trial: start → Investigator checkpoint → application shutdown during the Skeptic stage → restart (must call no model) → explicit continuation (must not re-run the Investigator) → completed report.
- Exits 0 only if at least one trial is fully GREEN. Host-rejected model output is reported as `RED-model`.

## Phase 4 — cancellation, execution projection, minimal UI

### Cancellation
- `POST .../investigations/{runId}/cancel` is authenticated and CSRF/Origin-guarded, and requires an `Idempotency-Key` and `{ "expectedAttemptNumber": n }`.
- The request:
  1. records durable **intent** (`cancel_requested_at_unix_ms` and a `cancellation-requested` event) in one fenced transaction;
  2. notifies the worker;
  3. returns `202`.

  It never finalizes the attempt and never releases the slot.
- **Running attempt:** the worker cancels the attempt token. Once the executor actually returns, its single terminal write records attempt `cancelled`, cause `user-cancelled`, run `cancelled`, and releases the slot. An uncooperative call keeps the slot held until it returns.
- **Queued attempt:** it is cancelled at the claim boundary, inside the claim transaction, before any model work begins.
- The heartbeat re-reads the durable intent, so a lost in-memory notification cannot lose a cancellation.
- Precedence of host-observed causes: stage-persistence halt > ownership loss > **user cancellation** > shutdown > deadline > checkpoint rejection > outcome.
- A cancelled run is terminal and is not continuable. A fresh investigation is a separate action.

### `GET .../investigations/{runId}/execution` (`gate-c6b-execution-projection-v1`)
Read-only and snapshot-scoped. It is derived only from host-recorded attempts, events, checkpoints and lineage; C5 report rows are never rewritten. It returns:
- run status and diagnostic, and a legacy flag;
- the current attempt and full attempt history (number, status, terminal cause, continuation link, epoch, deadline, cancel intent);
- the host-observed status of each stage: `not-started`, `running`, `accepted`, `accepted-earlier`, `reused-checkpoint`, `rejected`, `timed-out`, `failed`, `cancelled`, `abandoned` or `legacy-unavailable`, each with a checkpoint indicator (ID, SHA-256, acceptance attempt);
- **recovery eligibility**, which is separate from being interrupted. It runs the same checks as continuation, including full checkpoint revalidation, but only when the run is interrupted;
- `cancellable`, and whether this process owns execution.

### UI (minimal; no redesign)
- The investigation workspace shows an Execution panel with the stage cards, checkpoint SHA-256 indicators, the cause, eligibility with its reason, **Request cancellation**, **Continue investigation**, and the attempt history.
- Actions reuse one idempotency key per (action, run, expected attempt), so retrying a click replays rather than duplicating.
- The panel refreshes with the existing 2.5 s polling while a run is active.
- The investigation layout now stacks when it is rendered in a narrow panel.

### Coverage
`--gate-c6b-self-test` now runs 32 scenarios. The Phase 4 scenarios:
- an uncooperative call keeps the slot and blocks admission after cancellation; intent is idempotent and stale requests are refused; afterwards the attempt is `cancelled/user-cancelled`;
- cooperative cancellation is recorded as `user-cancelled` and leaves no checkpoint;
- a queued attempt is cancelled at the claim boundary with zero evidence opens or model calls;
- durable intent is honored without the notification;
- projection stages, checkpoints, eligibility and ineligibility (changed snapshot), the completed view, and scope isolation.

`tests/investigations/execution-control.test.mjs` exercises the real HTTP boundary:
- 401 without a session;
- 403 for missing or invalid CSRF and for a forged Origin;
- 400 without an idempotency key or with a missing or invalid expected attempt;
- 404 for foreign or unknown scope;
- a second API process on the same data directory reports `execution-lock-held-by-another-process` while the first stays healthy;
- restart on the same isolated directory reacquires ownership.

## Legacy compatibility

- C5 report rows and JSON are copied byte-for-byte, and their read endpoints are unchanged.
- C6a attempts are never made resumable.
- Pre-C6b incomplete records stay non-resumable.

## Deterministic verification (`--gate-c6b-self-test`)

Each scenario uses its own temporary data directory:

- v5 → v6 migration preserves history, and `Initialize` recovers nothing.
- The v5-checkpoint refusal rolls back.
- In-process lock exclusion and release.
- Epoch triggers.
- **Real cross-process ownership.** A child probe process holds the lock with a running attempt. The parent cannot acquire the lock or reconcile, and an expired heartbeat steals nothing. After the child is killed:
  - the parent gets epoch + 1 and the abandoned attempt becomes `interrupted`, with correct per-epoch event chronology;
  - every stale-owner write (heartbeat, claim, admission, stage event, interrupt, fail, complete, release) is rejected without appending events.
- The worker does not invoke the executor at startup.
- Exactly-once execution and idempotent replay.
- A non-owner refuses admission and execution.
- Shutdown with an uncooperative call keeps the attempt `running` and the slot held until the call returns, then records `application-shutdown`.
- Deadline cause.
- An injected failure of the terminal write: no second finalization, the slot stays held, admission is busy, and restart reconciliation produces no report.
- Classification precedence.

## Owner verification (Windows, PowerShell)

```powershell
cd C:\Users\dvdht\source\repos\CodeArcheologist
git fetch origin claude/loving-franklin-vq8fco
if ($LASTEXITCODE -ne 0) { throw "Fetch failed" }
git switch --detach origin/claude/loving-franklin-vq8fco
if ($LASTEXITCODE -ne 0) { throw "Checkout failed" }
.\scripts\check.ps1
if ($LASTEXITCODE -ne 0) { throw "C6b local verification RED" }
dotnet run --project .\tests\maf-adapter-contract\MafAdapterContractProbe.csproj --configuration Release --no-build -- --gate-c4-run 3
if ($LASTEXITCODE -ne 0) { throw "Genuine C4 MAF/Ollama regression RED" }
```

`check.ps1` now includes `--gate-c6b-self-test`. To run that test alone:

```powershell
dotnet run --project .\tests\maf-adapter-contract\MafAdapterContractProbe.csproj --configuration Release --no-build -- --gate-c6b-self-test
```

Phase 3 genuine MAF/Ollama walkthrough (needs local Ollama with `qwen3.5:9b`; can take several minutes per trial):

```powershell
dotnet run --project .\tests\maf-adapter-contract\MafAdapterContractProbe.csproj --configuration Release --no-build -- --gate-c6b-run 2
if ($LASTEXITCODE -ne 0) { throw "C6b genuine shutdown/restart/continuation walkthrough RED" }
```

### Manual restart-and-continuation walkthrough (B5)

Use a disposable data directory, never your real one:

```powershell
$env:CODE_ARCHEOLOGIST_DATA_DIR = Join-Path $env:TEMP ("ca-c6b-" + [guid]::NewGuid())
.\scripts\start.ps1
```

1. Pair the browser. Create a workspace, register a small trusted C# folder, capture a snapshot, open the AI investigation workspace and start an investigation.
2. Watch the Execution panel. Once the Investigator card shows **Checkpoint accepted** and the Skeptic card shows **Running**, stop the app (Ctrl+C).
3. Run `.\scripts\start.ps1` again with the same `$env:CODE_ARCHEOLOGIST_DATA_DIR`. Re-pair. The run should be **interrupted**, the cause `application-shutdown`, the panel **eligible**, and Ollama should show no new request.
4. Click **Continue investigation**. Expected:
   - attempt 2 appears;
   - the Investigator card shows **Revalidated checkpoint reused**;
   - the Skeptic and Reviewer run;
   - the run ends **completed** with the report.
5. Start another investigation and click **Request cancellation** while a role is running. The panel should show the cancellation as pending until the local call stops, then the run becomes **cancelled** (`user-cancelled`).
6. Afterwards, remove the variable and the temporary folder: `Remove-Item Env:CODE_ARCHEOLOGIST_DATA_DIR`.

On macOS or Linux, use `./scripts/check.sh`. Cross-platform lock verification needs `check.sh` on Linux **and** macOS. Linux has passed in the authoring container; macOS has not been run.
