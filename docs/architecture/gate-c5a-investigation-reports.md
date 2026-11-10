# Gate C5a — Durable evidence-linked report infrastructure

**Status:** Draft implementation; owner-local GREEN is required before merge.
**Base:** merged Gate C4. **Next:** C5b React investigation workspace, not C6.

## Architecture

The API adds three authenticated snapshot-scoped routes:

- `POST /api/v1/workspaces/{workspaceId}/repositories/{repositoryId}/snapshots/{snapshotId}/investigations` with JSON `{"entryPointId":"<existing graph node id>"}`, a new UUID `Idempotency-Key` header, the paired browser cookie, existing custom request header and CSRF header. Returns 202 with the report location or 200 for an idempotent replay. Only saved HTTP entry-point candidates are supported.
- `GET .../investigations` lists at most 50 recent investigation summaries.
- `GET .../investigations/{runId}` returns scoped status and, once terminal, the persisted report if one was validly produced.

The host validates snapshot ownership and immutable evidence before admitting an investigation. The local coordinator executes **one** bounded in-process task at a time, detached from request cancellation and governed by host shutdown plus a six-minute overall deadline. There are no retries and no work resumption after restart. A second concurrent run returns 409. The model is `qwen3.5:9b` at localhost Ollama, using existing C3/C4 execution; no new model access or cloud dependency was introduced.

## Persistence contract

The existing workspace SQLite database migrates from schema 3 to schema 4, adding a report ledger with run/workspace/repository/snapshot/fingerprint/entry-point IDs, report version, start/finish timestamps, safe status diagnostics, idempotency key/digest and bounded structured JSON for completed or incomplete terminal outcomes. On startup, orphaned `running` records become `incomplete` with the explicit diagnostic `host-restarted-before-completion`, and their absent report remains absent. Historical completed reports are retained.

The host constructs a `gate-c5-investigation-report-v1` report only from the C4 runner results. It checks role/run/snapshot scope and completed Reviewer reconciliation. A completed orchestration means **validated workflow**, not verified semantic/runtime behavior. Incomplete runs retain their real role statuses and no invented final review.

Each independently validated role carries a host-resolved canonical evidence ledger. A handle such as `E1` belongs to a specific role/run/session; `Investigator/E1` and `Skeptic/E1` may identify different source facts. Persisted links retain the original evidence kind/status and source file/hash/line/column. Model-generated handles do not establish correctness without the host catalog.

## Deliberate C5a limitations

- No frontend changes or UI integration; C5b will consume the API.
- Live status in C5a is `running`, followed by a terminal status. Detailed live role-by-role event streaming is not introduced.
- A report's source references remain anchored to the original snapshot. A report read does not independently prove its saved source bytes are currently available; C5b must verify navigation targets through existing snapshot-bound evidence access and show a stale/unavailable state rather than falling back to mutable sources.
- No workflow checkpoint/resumption, asynchronous multi-user scheduling, arbitrary repository code execution, semantic proof, richer analyzer tools, cross-repository investigation, or comparative benchmarks.
- Host-generated generic failure diagnostics intentionally exclude source excerpts, provider exception messages, and raw prompts.

## Owner verification

From the feature branch, run `./scripts/check.ps1` on Windows. This includes a C5 model-free probe exercising the schema upgrade, idempotency, scope boundaries, role-local evidence identity, persistence, terminal-state protection and restart recovery. Existing API/frontend/security/snapshot tests remain required.

After local GREEN, separately run the genuine C4 Ollama probe to confirm no regression:

```powershell
dotnet run --project .\tests\maf-adapter-contract\MafAdapterContractProbe.csproj --configuration Release --no-build -- --gate-c4-run 3
```

Do not merge without both gates GREEN. The subsequent C5b milestone must add real HTTP/UI-driven end-to-end investigation integration and regression tests.
