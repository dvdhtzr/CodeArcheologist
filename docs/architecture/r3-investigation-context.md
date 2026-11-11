# R3 PR1 — Versioned Investigation Context and Host Validation

**Status:** Draft implementation, awaiting owner-local GREEN. **Approved architecture:** R3 D1–D10. **Base:** R2 merged PR #60. No source, model, evidence, agent, or repository execution behavior changes in PR1.

## PR1 purpose and scope

The current Gate C admission accepts only a verified saved-snapshot HTTP candidate and always answers a fixed host-authored question. R3 makes the developer question a first-class, durable identity without changing that established inference protocol yet.

PR1 adds **only** the supported structured question kind `explain-http-entry-point`, applicable to existing `http-endpoint-attribute` and `http-minimal-mapping` node IDs. Arbitrary methods, free-text questions, paths, plugins, dependencies and new model tools are explicitly unsupported.

## Context contract

`r3-investigation-context-v1` is an immutable host-authored record containing:

- `questionId` (new durable GUID independent of `runId`), `runId` and exact `GateCScope`: workspace, repository, snapshot, SHA-256 fingerprint, existing entry-point ID and evidence-tool version.
- `subjectNodeId` (must equal the original `GateCScope.EntryPointId`) and a **host-derived** `subjectNodeKind`.
- `questionKind=explain-http-entry-point`, pinned `questionTemplateVersion`, `capabilityPolicyVersion` and `budgetProfileVersion`.
- UTC creation time and SHA-256 digest of all the serialized logical identity fields.

The hash verifies record consistency and detects accidental alteration; it is **not an authentication MAC** against an attacker with write access to the SQLite database. The database remains protected by the existing local access boundary and append-only triggers. Version identities are pinned in the v1 read contract, so future runtime capability upgrades do not retroactively reinterpret historical runs.

Only the trusted host creates question IDs, chooses the subject kind and establishes the source scope. The initial client request contains only the supported question kind and a saved node ID. The host independently re-reads the entry-point package from the original verified snapshot and uses the existing fenced admission.

## SQLite v7 and legacy compatibility

`WorkspaceStore.Initialize()` migrates SQLite v6 to **v7** in its existing forward-only transaction. This creates the `investigation_question_contexts` table (one context per run) with immutable update/delete triggers, an independently stored digest, and an enforced foreign key to `investigation_reports`.

No existing report, attempt, event, checkpoint, lock, ownership epoch or replay row is rewritten by this migration. Historical Gate C runs have **no** R3 question context. They remain readable and cannot be relabeled as if they were originally asked a versioned R3 question.

`CreateInvestigation(..., questionSubjectKind)` retains the original request digest when `questionSubjectKind` is null. Structured requests use a separate, version-bound digest including the approved question and actual verified node kind. New context and new run/attempt/slot/event are committed **in the same fenced transaction**. Identical retries replay their original run/question IDs; replaying a legacy key as a structured question, or changing the question subject under the same key, returns 409. A busy slot and a stale owner do not create context records.

## Authenticated API endpoints

`POST .../investigations/questions`
- Request: `{"subjectNodeId":"<saved candidate ID>","questionKind":"explain-http-entry-point"}`
- Requires the existing authenticated session, origin/CSRF validation and UUID Idempotency-Key.
- Host validates the saved snapshot and question node with `SnapshotEvidenceTools.ReadEntryPoint`.
- 202: `{investigation,questionContext}` with the run URL in Location; 200: same on an identical idempotent replay; invalid/foreign/unsupported subjects fail before admission.
- Execution **still uses** the unmodified C6b worker, Investigator, Skeptic and Reviewer. No new model prompts or adaptive retrieval.

`GET .../investigations/{runId}/question-context`
- 200 `{status:"verified",context:...}` for a new versioned run after exact-scope/digest revalidation.
- 200 `{status:"legacy-no-question-context",context:null}` for an existing legacy run.
- 404 for an unknown/foreign run; 409 when stored context provenance cannot be verified.

Existing `POST .../investigations`, `GET .../investigations`, `GET .../investigations/{runId}`, worker endpoints, report contracts and checkpoint read/restore paths are unchanged.

## Model-free acceptance

The `--r3-question-context-self-test` probe validates:
- v6-to-v7 forward migration with an old `running` run; no implicit recovery or invented question.
- OS lock/SQLite fenced admission; one atomic context per new run; independent run/question identities.
- Replay vs conflict across question subject kinds and across legacy/new request families, including when the execution slot is busy.
- Readback after reopening SQLite; foreign workspace, snapshot and run identity rejection.
- DB-level immutable row triggers, corrupted digest, foreign scope, changed prompt template and unsupported question rejection.

The `tests/api/investigation-question.test.mjs` integration verifies auth, origin, idempotency headers, exact saved snapshot and unsupported question/subject rejection, and OpenAPI route registration. It intentionally submits **no successful HTTP investigation** because that would cause the genuine hosted worker to invoke local Ollama. Model-free success paths are exercised directly against the real transactional store; PR2 will add full UI admission and end-to-end Ollama acceptance.

## Owner verification (Windows)

```powershell
git fetch origin
git switch feature/r3-investigation-context-pr1
git pull --ff-only
dotnet run --project .\tests\maf-adapter-contract\MafAdapterContractProbe.csproj --configuration Release -- --r3-question-context-self-test
node --test .\tests\api\investigation-question.test.mjs
.\scripts\check.ps1
```

The HTTP test requires the trusted API Release DLL already built; run `dotnet build .\src\CodeArcheologist.Api\CodeArcheologist.Api.csproj --configuration Release` before `node --test` if not already built. Stop the API on port 5127 before running the HTTP test.

**Pass criteria:** no weakening of legacy C6b checkpoint or idempotency behavior, zero model calls during the new model-free tests, successful full local GREEN, no writes to registered source folders, and owner-controlled merge. No release, runtime inference, adaptive agent, or packaging readiness claim is made by this PR.

## Next milestones — design-only until separate approvals

- **PR2:** Atlas → versioned question admission → Observatory/Findings → Atlas navigation with snapshot identity preserved.
- **PR3:** Gap-directed retrieval, typed gaps, durable request receipts, revalidation on continuation and deterministic stopping, with independent Investigator/Skeptic sessions.
- **PR4:** Pre-registered comparative evaluation against current Gate C and deterministic baselines, on eligible fixtures and pinned curated repositories.

PR3's adaptive requests will require new versions of the evidence and checkpoint contracts. They must not reinterpret PR1 historical contexts or C6b accepted checkpoints.
