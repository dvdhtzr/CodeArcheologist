# V2 PR3b — Fenced Model Handoff Observation

**Status:** Draft implementation. Owner-local Windows GREEN pending.
**Baseline:** V2 PR3a merged at `c5f2c9f`. This is not a scored
experiment and does not authorize any adaptive comparative study.

## Goal

Record whether relevant evidence was actually presented to the model adapter
rather than merely *retrievable* (PR3a). The host still cannot know whether
the local provider received, parsed or used every byte. Never label a prepared
request, a tool return or an adaptive receipt as successful semantic delivery.

## Authority, storage and opt-in

The additive v9 SQLite migration creates append-only
`v2_handoff_optins` and `v2_handoff_events`. No v2/v3 checkpoints,
R3 receipt/gap tables, C6c corpus or PR4 outcomes are changed. Current DBs
migrate normally; old migration fixtures remain inert without opt-in.

Observation is enabled only by an explicit, fenced host-only
`WorkspaceStore.EnableV2Handoff(runId,fence)` **before a queued attempt is
claimed**. There is deliberately no REST toggle or browser surface. When
disabled, the real model calls run unchanged, with no new observation writes.
All observed events must pass the current OS-lock/SQLite-epoch attempt fence.

The host records only call/run/attempt/role identity, per-role ordinal,
stage, snapshot fingerprint, exact SHA-256 of the *string passed to the
model adapter*, immutable host evidence-link metadata, existing adaptive
receipt IDs, lifecycle state and a content digest. No raw prompts, source
excerpts, response text or model reasoning are saved in this ledger. Evidence
links can include file paths, which remain local.

An adaptive observation **references** the authoritative, committed
`adaptive_evidence_receipts` chain; it never creates a competing receipt.
When the model prompt contains source evidence, the host independently
checks its serialized run/fingerprint/role and exact role-scoped handles.
The Reviewer has no direct source evidence in this facility.

## Lifecycle: do not overstate

1. `prepared`: prompt and evidence references verified, fenced commit
   succeeded, and **model not yet called**.
2. `adapter-entered`: the delegate that initiates the model invocation has
   **actually been called**, and that fact committed. This is not proof
   of transport delivery or processing.
3. `completed` / `faulted`: a successful response or exception observed
   by the caller, after adapter-entry, with a fenced terminal observation.

A crash or failed write can leave a prefix of these events. **Missing
records are unknown**, not evidence the model did nothing. If a prepared
write fails, no call can start. If entry persistence fails after the
model call has begun, the in-flight task is awaited and the evaluated worker
fails closed. Likewise, failed completion persistence halts rather than
publishing a success. Provider failures retain their separate failed status.

There is no post-hoc credit for reused checkpoints. The pre-existing
`checkpoint-reused` attempt lineage points back to the original accepted
checkpoint; no model entry event is manufactured for the continuation.

These SHA-256 digests detect unexpected alteration; they are not external
signatures or protection from an administrator who controls the database.

## Local tests / live probe

Run the full trusted-repository gates:

```powershell
git fetch origin
git switch --detach origin/feature/v2-pr3b-fenced-model-handoff
.\scripts\check.ps1
```

`check.ps1` includes `--v2-handoff-self-test`. The tests use a disposable
SQLite database, real ownership fencing and role sessions, but **no Ollama**.
They exercise valid handoff sequences, tamper-proof-by-app event tables,
injected prepared/entered/completed write failures, provider exception,
stale fence, reviewer source isolation, adaptive receipt requirement and
exact prompt/snapshot identity.

**Only after full local GREEN**, invoke a single real-Ollama diagnostic
smoke test (one trial, not scored):

```powershell
dotnet run --project .\tests\maf-adapter-contract\MafAdapterContractProbe.csproj --configuration Release -- --v2-handoff-run 1
```

The smoke uses exactly the normal read-only synthetic HTTP fixture and
managed adaptive Gate C runner. It explicitly enables observation before
the worker claims the attempt, then requires a completed run, three valid
v3 checkpoints, revalidated original R3 receipts, separate role handoffs,
bounded call counts, and role-isolated Reviewer provenance. Its temporary
workspace database is discarded after the run. It does NOT use or modify
the user's actual workspace.

Existing `--r3-adaptive-run 1` remains the unchanged historical smoke
test and does not enable V2 observation.

## Scientific and implementation limits

- A provider-boundary call is not provider acknowledgment.
- Fixed Gate C has no historical per-request persisted receipts; observed
  host evidence links and exact prompt context are the available provenance.
  Do not retroactively credit fixed evidence as committed adaptive receipts.
- V2 PR3a's pre-model retrievability remains distinct from real handoff.
- This step is an observation mechanism, not source-ground truth, answer
  quality, statistical comparison, or an approved held-out run.
- The durable v9 rows are read-only outside their fenced host writes;
  no endpoint exposes them yet. A future V2 scoring projection must
  separately bind trusted PR2 criteria and PR3a mappings to exact events.
- All V2 evaluation remains **NOT READY FOR SCORING**.

## Merge gate

Owner's complete `check.ps1` GREEN, fault-injection acceptance and
a genuine Ollama smoke are required, followed by architectural review and
separate approval to merge. No implementation of PR4 or new scored trial
is authorized here.
