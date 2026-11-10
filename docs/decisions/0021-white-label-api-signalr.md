# ADR 0021 — White-label API with SignalR push (W1)

- **Date:** 2026-11-10
- **Status:** Decisions approved by the owner. W1: owner reported `check.ps1` GREEN on Windows; Linux passed in the authoring container.
- **Context:** The owner is building a separate front end in parallel. Code Archeologist becomes a white-label API, and the work here focuses on the system's capabilities.

## Decisions (approved by the owner)

1. **Boundary: same machine, any configured origin.**
   - The API stays loopback-only (`127.0.0.1:5127`), and Host checks are unchanged.
   - Browser front ends may call it only from loopback origins the owner lists explicitly, in `<data dir>/client-origins.json` (or `CODE_ARCHEOLOGIST_CLIENT_ORIGINS` for disposable test processes). No wildcards, remote hosts or paths are accepted; invalid configuration stops startup.
2. **Authentication: pairing code exchanged for a bearer token.**
   - `POST /api/v1/auth/token` exchanges the console pairing code for a random 256-bit token. The token lives in memory only, is bound to the process, lasts 30 minutes and is revocable. Only its SHA-256 is retained, and it is subject to the same lockout as login.
   - REST clients send `Authorization: Bearer`. The SignalR hub also accepts the token as the `access_token` query parameter, on hub paths only.
   - Bearer requests carry no ambient credential, so they need no CSRF token or custom header. Origin allowlisting, idempotency keys and optimistic concurrency still apply to every mutation.
3. **SignalR is server-to-client push only.**
   - All commands stay REST. The only hub methods are `Subscribe` and `Unsubscribe`, and each subscription is checked against the exact stored workspace/repository/snapshot/run scope.
   - Messages carry committed host ledger events and the versioned execution projection.
4. **Priorities.** The bundled React client is frozen and kept only as a regression harness, using the unchanged cookie + CSRF path. C6c-3 is deferred. Next work exposes the analysis and investigation capabilities through this API.

## Delivery guarantees

- Events are read from SQLite after commit, so nothing uncommitted is ever pushed.
- `Subscribe` joins the run's group *before* reading the backlog, and the publisher never skips events. Delivery is therefore gap-free and at-least-once, ordered per attempt; clients de-duplicate by `eventId`.
- The REST events and execution endpoints are the resync path. The integration test asserts that pushed events equal the REST ledger.
- SignalR detailed errors are disabled: clients only ever see fixed codes such as `run-not-found-in-scope`.

## Unchanged

The cookie + CSRF path, loopback binding, Host validation, execution ownership, fencing, idempotency, and every evidence rule (AGENTS 1–57).

## Addendum — W2 capability resources (approved 2026-11-10)

- **New read-only resources** (`code-archeologist-capabilities-v1`): `entry-points`, `methods`, and `impact?methodId=`.
  - They are server-side projections of the existing evidence graph.
  - `impact` applies the ADR 0015 one-hop rule, with explicit unknown and limit fields.
  - No analyzer, edge kind or status was added.
- **Entry-point evidence:** the existing `evidence-tools/entry-point?entryPointId=` endpoint is documented rather than duplicated. Node IDs contain `/` and `:`, so they are query parameters.
- **Snapshot push** (`SubscribeRepository` / `snapshotCaptured`): sent only after a snapshot is committed, consistent with rule 59. No progress percentages are sent (rule 3). The baseline is taken at subscribe time, so a snapshot committed before the publisher's first poll is still pushed.

