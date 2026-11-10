# Gate C6b — Managed worker and explicit recovery

Status: **in progress on a draft PR.** Architecture approved by the owner (decisions D1–D5). Phases 1–2 are implemented here. Phases 3–4 have not started. Nothing in this document is GREEN until the owner has run the commands below and reported the actual output.

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
| 1 | SQLite v6, OS lock, fenced ownership, startup reconciliation, ownership tests | implemented; Linux self-test passed in the authoring container; owner verification pending |
| 2 | Managed worker replaces `Task.Run`; single terminal write; shutdown/deadline semantics | implemented; same verification state |
| 3 | Role checkpoints, evidence-handle reconstruction, compatibility checks, `POST .../continue` | not started |
| 4 | Cancellation intent, `GET .../execution`, `POST .../cancel`, minimal UI, real Ollama walkthrough | not started |

## Responsibilities

1. **Admission** — `InvestigationAdmissionService`. The endpoint handles authentication, CSRF, scope checks and the idempotency key. The service does a fenced, idempotent, transactional admission and then sends the worker a wake-up signal. It never runs MAF. Only the current fenced owner can admit work. Any other process answers `503`.
2. **Managed worker** — `InvestigationWorker` (hosted service). It handles claiming, the per-attempt deadline (6 minutes by default), the heartbeat, shutdown and the single terminal write.
3. **Checkpoint-aware coordinator** — Phase 3. Until then the worker calls the unchanged C3/C4 runners through `IInvestigationExecutor` (`MafInvestigationExecutor` in production). Role timeouts (90 s) and the bounded retry rules are unchanged.
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
  | Role timeout / provider error / validation rejected | `incomplete` | `incomplete` | `role-timeout` / `provider-error` / `validation-rejected` |
  | Evidence rejected | `rejected` | `rejected` | `evidence-rejected` |

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

On macOS or Linux, use `./scripts/check.sh`.
