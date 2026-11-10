# Gate C6a — Durable execution contracts and events

Status: implementation candidate for local owner GREEN; not yet approved for merge.
Parent design: C6 approved November 10, 2026. Admission: **reject new requests immediately** while an execution is active. Replays return their original record.

## Scope

C6a adds a SQLite v5 persistence contract, one durable initial attempt for each new investigation, a transactional host event ledger, an execution-slot admission constraint, scoped event reads, and deterministic coverage. C5 report JSON and independent MAF Investigator / Skeptic / Reviewer semantics remain intact. C6a does **not** install a hosted worker, persist role-level checkpoints, implement continuation, or synthesize role progress from model prose.

## Contracts

- Attempt: `gate-c6-execution-attempt-v1`.
- Event: `gate-c6-investigation-event-v1`.
- Existing report: `gate-c5-investigation-report-v1` (unchanged).
- Each attempt belongs to one immutable existing `run_id`.
- Each event is scoped through its attempt to the saved snapshot and numbered monotonically within its attempt.
- Role checkpoints have a reserved immutable table but are **not written by C6a**.

Allowed attempt transitions: queued -> running/cancelled/rejected; running -> completed/incomplete/interrupted/cancelled/rejected/failed. Terminal states cannot be reopened. Manual continuation through another attempt remains **C6b**.

Persisted C6a event types: `attempt-admitted`, `attempt-started`, `attempt-completed`, `attempt-incomplete`, `attempt-cancelled`, `attempt-rejected`, `attempt-failed`, `attempt-interrupted`. The broader role/evidence/checkpoint taxonomy will only be emitted after C6b provides host observation and validation hooks.

## Atomic invariants

1. An immediate SQLite transaction creates the C5 report row, initial attempt row, reserves the singleton local execution slot, and appends `attempt-admitted`. Failure rolls everything back.
2. Exact idempotent replays return their existing record **before** considering the occupied slot; a reused key with a different request digest conflicts.
3. Competing new requests return busy without creating ghost investigations.
4. Starting execution updates the attempt and commits `attempt-started` together.
5. Terminalization updates the original C5 report and the attempt, appends the terminal event, and releases the slot **within one transaction**.
6. On process restart, pre-C6 C5 records are finalized using legacy semantics. New queued/running attempts are explicitly finalized with host restart diagnostics and one event; they are never silently restarted or made resumable.
7. Event and checkpoint rows cannot be UPDATEd or DELETEd. A SHA-256 checkpoint digest is **not** proof against an adversary with local database write access.
8. The read endpoint `GET .../investigations/{runId}/events` is authenticated and scoped to the workspace/repository/snapshot.

## Operational limitation carried into C6b

The active execution remains the existing detached C5 task, so a persisted running state does not imply a live model call. The slot is a database admission exclusion mechanism, not a fully fenced, leased worker. Startup restart handling assumes a single actively running application instance; **a second simultaneously starting application could classify the first instance's C6 attempt as interrupted**. C6b must establish process ownership fencing/leases before claiming cross-process safe recovery. Do not advertise or rely on multi-process recovery guarantees from C6a alone.

## Compatibility

C5 report contract and read endpoints are unchanged. Existing v4 report rows are not converted into attempts or given imaginary event history. The migration runs in the existing transactional `WorkspaceStore.Initialize()` sequence and increments SQLite `user_version` to 5. The current Windows Release startup fix remains untouched.

## Owner verification

```powershell
cd C:\Users\dvdht\source\repos\CodeArcheologist
git fetch origin
if ($LASTEXITCODE -ne 0) { throw "Fetch failed" }
git switch --detach origin/feature/gate-c6a-durable-execution-contracts
if ($LASTEXITCODE -ne 0) { throw "Checkout failed" }
.\scripts\check.ps1
if ($LASTEXITCODE -ne 0) { throw "C6a local verification RED" }
```

Run the genuine C4 MAF/Ollama regression as an extra assurance of unchanged orchestration:

```powershell
dotnet run --project .\tests\maf-adapter-contract\MafAdapterContractProbe.csproj --configuration Release --no-build -- --gate-c4-run 3
if ($LASTEXITCODE -ne 0) { throw "Genuine C4 MAF/Ollama regression RED" }
```

Manual observation: start one investigation; check read-only events API for admitted/started/terminal transitions in order; reopen the existing report in C5b UI. Replay same idempotency key while active should return the original run; a different new key while busy must return 409 and leave no ghost report.

**No claim of GREEN** until these checks actually pass. Any RED must be fixed on the same branch and rerun before marking the draft PR ready.
