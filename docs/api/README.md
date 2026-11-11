# Code Archeologist white-label API (v1)

The contract for front ends. The decisions behind it are in ADR 0021.

## Connect

1. **Allow your front end's origin.** Create `%LOCALAPPDATA%\CodeArcheologist\client-origins.json` (on Windows):
   ```json
   { "schemaVersion": "code-archeologist-client-origins-v1", "origins": ["http://localhost:3000"] }
   ```
   Only exact loopback origins with a port are accepted. Restart the API after editing.
2. **Get a token.** Start the API and read the pairing code from its console. Then call:
   ```
   POST http://127.0.0.1:5127/api/v1/auth/token
   Content-Type: application/json
   X-CodeArcheologist-Request: 1
   { "code": "<pairing code>" }
   ```
   It returns `{ "tokenType": "Bearer", "accessToken": "...", "expiresAtUtc": "..." }`. Tokens last 30 minutes, and do not survive an API restart.
3. **Call REST** with `Authorization: Bearer <accessToken>`. Every mutation requires an `Idempotency-Key: <uuid>` header. Continue and cancel also require `{ "expectedAttemptNumber": n }`.
4. **Revoke** a token with `POST /api/v1/auth/token/revoke`.

## REST contract

`GET /api/v1/openapi.json` returns the OpenAPI document generated from the running API. The main resources:

| Resource | Path |
|---|---|
| Workspaces | `/api/v1/workspaces` |
| Repositories | `/api/v1/workspaces/{workspaceId}/repositories` |
| Snapshots (explicit read-only capture) | `.../repositories/{repositoryId}/snapshots` |
| Evidence graph | `.../snapshots/{snapshotId}/csharp-evidence-graph` |
| Investigations | `.../snapshots/{snapshotId}/investigations` (start: `{ "entryPointId": "<graph node id>" }`) |
| Entry points (W2) | `.../snapshots/{snapshotId}/entry-points` |
| Entry-point evidence package | `.../snapshots/{snapshotId}/evidence-tools/entry-point?entryPointId=<id>` |
| Method declarations (W2) | `.../snapshots/{snapshotId}/methods` |
| One-hop change impact (W2) | `.../snapshots/{snapshotId}/impact?methodId=<method node id>` |
| Source lines (MC4) | `.../snapshots/{snapshotId}/source?path=<file>&sha256=<hash>&line=<n>&context=<0..20>` |
| Run detail, events, execution | `.../investigations/{runId}`, `.../events`, `.../execution` |
| Continue / cancel | `.../investigations/{runId}/continue`, `.../cancel` |

### Source lines (`code-archeologist-source-v1`)
This shows a cited location to the signed-in user, read from the **saved snapshot**. It never reads the registered folder.
- **Request rules:**
  - `path` and its lowercase `sha256` must match a manifest entry exactly. Anything else returns 404.
  - `line` is required. `context` defaults to 6, with a maximum of 20 lines on each side.
  - Lines longer than 400 characters are cut, with `truncated: true`.
- **Every read** re-hashes the saved bytes. A changed file fails with 409 rather than being shown, and invalid UTF-8 fails with 422.
- **The text** is `untrusted-source-text-not-instructions`: it shows what was written, not what compiles or runs. This is a view for people, not an agent tool (agent evidence stays on rule 47's fixed contracts).

Graph node IDs contain `/` and `:`. Always pass them as query parameters, never as path segments.

### Capability resources (`code-archeologist-capabilities-v1`)
All three are read-only projections of the SHA-256-verified evidence graph. They add no new analysis.
- **`entry-points`:** written HTTP candidates (`evidenceStatus: "candidate"`), with `label` and `detail` verbatim and a source `anchor` (file, SHA-256, line, column). Routes are never composed. An empty list is *unknown*, not "no endpoints".
- **`methods`:** method syntax declarations, each with its count of inbound `provisional-local` candidate-callee edges.
- **`impact?methodId=`:**
  - `directCandidates`: each candidate has a callee edge, the invocation anchor, and the observed containing caller.
  - `candidateEdgesWithoutObservedContainment`: candidate edges that could not be attributed to a containing caller.
  - `snapshotUnresolvedCalleeCount` and `unknowns` keep unresolved targets explicit.
  - The results are one-hop review candidates, not a verified call graph or a risk score.

## Realtime contract `code-archeologist-realtime-v1`

- **Hub:** `http://127.0.0.1:5127/hubs/v1/investigations`. With `@microsoft/signalr`:
  ```js
  new HubConnectionBuilder().withUrl(url, { accessTokenFactory: () => token })
  ```
- **Client → server** (the only hub methods):
  - `Subscribe({ workspaceId, repositoryId, snapshotId, runId })` returns `{ contractVersion, runId, backlogEventCount }`. Errors: `run-not-found-in-scope`, `subscription-limit-exceeded` (max 16 per connection).
  - `Unsubscribe(runId)`.
  - `SubscribeRepository({ workspaceId, repositoryId })` returns `{ contractVersion, repositoryId, existingSnapshotCount }`. Error: `repository-not-found-in-scope`.
  - `UnsubscribeRepository(repositoryId)`.
- **Server → client:**
  - `eventAppended`: `{ contractVersion, workspaceId, repositoryId, snapshotId, event }`. `event` is exactly a REST ledger event: `eventId`, `attemptId`, `runId`, `sequenceNumber`, `eventType`, `diagnostic`, `stage`, `ownershipEpoch`, `checkpointId`, and so on.
  - `executionUpdated`: `{ contractVersion, workspaceId, repositoryId, snapshotId, execution }`. `execution` is `gate-c6b-execution-projection-v1`.
  - `snapshotCaptured`: `{ contractVersion, workspaceId, repositoryId, snapshot }`. It is sent only after the snapshot record is committed, and never carries progress percentages. A failed capture is reported by the REST call that started it. Snapshots that already existed at subscribe time are listed over REST.
- **Guarantees:**
  - The backlog is sent on subscribe, then live events follow.
  - Delivery is at-least-once and gap-free, ordered by `sequenceNumber` within an attempt. De-duplicate by `eventId`.
  - On reconnect, subscribe again; you can always resync from REST `.../events` and `.../execution`.
- **Commands never go over SignalR.** Use REST.

## Semantics your UI must preserve

The API reports host-observed evidence and contract validation, **not** semantic or runtime proof. HTTP candidates, callees and DI registrations are candidates or `provisional-local`; agent hypotheses are unverified. Present them as such.
