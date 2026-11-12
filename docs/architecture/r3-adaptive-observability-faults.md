# R3 PR3d — Adaptive Evidence Activity and Integrity Faults

**Baseline:** merged PR #65, `main` at `5119dbcee056544e8b3b663754c8de960d8ba3fb`.
**Branch:** `feature/r3-adaptive-observability-faults-pr3d`.
**State:** draft implementation, pending owner-local verification. Do not merge until owner reports complete GREEN and separately approves merging.

## Architectural boundary

The existing R3 PR3a (v8 append-only ledger), PR3b (receipt replay and v3 checkpoint), and PR3c (experimental host-governed adaptive MAF execution) remain authoritative. The v0.1 and v0.2 specifications, accepted ADRs, `AGENTS.md`, model prompts, budgets, checkpoint contract bytes, database schema and all 50 owner-approved corpus labels are unchanged.

Only preexisting immutable snapshot evidence is read, with the original scoped and role-isolated evidence factory. The operational worker retains its OS lock / SQLite epoch fence and **manual**, explicitly requested continuation. The legacy Gate C and stable R3 v1 paths remain separate. Model-authored gap kind and purpose cannot confer evidence authority. No new evidence tool, plugin, source execution, external provider, schema migration, or transport is introduced.

## Versioned read-only API

`GET /api/v1/workspaces/{workspaceId}/repositories/{repositoryId}/snapshots/{snapshotId}/investigations/{runId}/adaptive-activity`

- Requires the existing bearer-session authorization and origin policy. It is a **separate** projection from `/execution`; Gate C's historical event and execution contracts remain unchanged.
- For a run in an exact saved workspace/repository/snapshot, first verify its adaptive v2 context, immutable snapshot identity, attempt ownership, ordered host events and epochs, and accepted v3 checkpoint event lineage. Checkpoint verification reopens and replays saved snapshot evidence.
- For each role in each attempt, read the existing durable receipt and gap readers, verify their SHA-256 digests, ownership epoch, exact request/reply and evidence mapping by independent source replay, and validate the gap lifecycle and ordering.
- Respond with `r3-adaptive-evidence-activity-v1`, the scoped run and snapshot IDs, attempts, per-role initial/follow-up capabilities and ordinals, receipt IDs, cumulative handle counts, newly issued handle counts, separately labeled model-proposed gap KIND, mapped capability, host allowance or denial, new/no-new evidence, checkpoint accepted/reused, and only durably supported stage termination reason.
- **Unrecorded / unknown** means *no persisted proposal*. It is **not** evidence that the model proposed `none`. The PR3c `none` path does not persist a gap event; absent a durable event, `none` cannot honestly be displayed.
- No arbitrary source text, raw prompt, raw model response, user-authored purpose or unlimited ledger JSON appears in the projection.
- A missing run or non-adaptive run returns 404; malformed or inconsistent durable provenance raises a 409 integrity error; non-integrity availability failures raise 503. Neither should produce a success-shaped empty verified history. An admitted run before any evidence acquisition can legitimately have zero receipts: display as unrecorded.
- The recorded SHA-256 digests detect unexpected corruption; they are **not** tamperproof signatures against a database administrator. A total removal of an uncheckpointed receipt chain with no independent audit marker cannot always be distinguished from a model failure before acquisition; do not report verified absence as a fact.

## Mission Control

The **Adaptive Evidence Activity** panel appears for experimental investigation history, not stable runs. It uses the paired `ApiClient` and the current snapshot scope and checks the version and run/snapshot IDs again before display. It queries REST as the authority after the existing SignalR-assisted execution updates and on explicit refresh. A failed request, unexpected contract, or integrity error clears the earlier verified display and shows a visible error. Interrupted histories preserve all verified receipts from earlier attempts, including checkpoints revalidated and reused on an explicit later attempt.

Model proposals are visually separated from host decisions, and zero new handles is distinct from no recorded request. The existing Atlas, Observatory and Findings navigation, source scoping, pairing, session expiry, and single realtime protocol are untouched.

## Model-free fault injection and actual delivery seam

`IAdaptivePromptClientFactory` wraps the unchanged production MAF/Ollama agent and structured response schemas. A deterministic probe substitutes a prompt client at the actual point the adaptive runner would deliver source evidence to a model; it counts model-facing evidence prompts and reads the persisted ledger **before** allowing delivery. It is not a fake role claiming the guarantee. Unexpected host exceptions propagate as persistence/stage failures, while genuine provider/adapter exceptions get their separate classification.

The dedicated `--r3-adaptive-fault-self-test` has isolated disposable SQLite scenarios with fault triggers and assertions that:
- an injected receipt INSERT abort yields **zero model-facing evidence prompts** and no successful checkpoint;
- a gap-event INSERT failure rolls back the follow-up receipt transaction and prevents the final model prompt;
- a stale epoch/owner cannot persist or deliver evidence;
- cancellation during an active model stage leaves any previously committed receipts but does not generate a checkpoint;
- a persisted receipt without a gap event remains unrecorded, never `none`;
- missing/reordered/mutated receipts and gap events cannot be projected as verified (including an event whose row digest was recomputed to match a malicious reorder).

Existing C6b/R3 ledger and adaptive execution suites additionally cover deadline and explicit manual continuation, missing gap event, checkpoint v2/v3 compatibility, replay refusal, no-new-evidence and denied duplicate proposal without a tool call, restart fencing, checkpoint reuse, and role isolation. The HTTP integration suite checks bearer rejection, foreign or unknown scope and OpenAPI registration.

**No Ollama model is required for the fault suite.** The separate genuine Ollama trial is still mandatory owner verification and not a proof of semantic improvement.

## Owner verification (Windows)

```powershell
git fetch origin
git switch feature/r3-adaptive-observability-faults-pr3d
git pull --ff-only
.\scripts\check.ps1
```

Only after a complete local GREEN, execute genuine functional acceptance separately:

```powershell
dotnet run --project .\tests\maf-adapter-contract\MafAdapterContractProbe.csproj --configuration Release -- --r3-adaptive-run 1
```

When RED, preserve and report the first failing diagnostic. Stop standalone API/Vite servers on ports 5127/5175 before browser checks. No PR4 comparative evaluation or model prompt editing belongs here.

## Small manual browser acceptance (disposable local snapshot)

1. Pair Mission Control. Capture a disposable C# HTTP-candidate fixture and explicitly use Atlas **Investigate experimentally (adaptive)**. After successful completion, select that saved run and open **Adaptive Evidence Activity**; verify role-specific ordered receipt IDs, capabilities, cumulative/new evidence counts and separate proposal/host-decision labels.
2. For a deterministically denied duplicate gap (the existing model-free fixture scenario), use its resulting saved run in an isolated test database or an explicit accepted test fixture; the activity panel must show `proposed` + `denied` and **no** extra follow-up receipt or new evidence. Real model behavior is not guaranteed to choose a duplicate.
3. Interrupt an adaptive run at a role stage, restart the API under a new owner, and select the historical run. Previously committed receipts should remain visible as historical verified records, interrupted status visible, and no new role automatically started. Only the explicit continuation button can try replay-validated reuse.
4. Switch to another snapshot/repository and try to inspect the original run (including a direct scoped GET). It must not show the original activity: expect 404 for an out-of-scope URL; switching back restores scoped history.
5. In a disposable database **only**, inject a missing/corrupted receipt or gap event after creating an adaptive run, and refresh the panel. Expect a visible integrity failure, not an empty verified-history state, and no accepted evidence/checkpoint based on that invalid record.

## Exit criteria

Owner verifies receipt provenance and no fabrication, real pre-delivery persistence failure behavior, fail-closed corrupted ledgers and checkpoint compatibility, recovery, complete local GREEN, genuine MAF/Ollama acceptance, and browser behavior. **PR3d remains draft until that approval.** Next *after* merge: R3 PR4 preregistered comparison against fixed Gate C, Gate B and deterministic baseline, without changing the existing labels or scoring thresholds.
