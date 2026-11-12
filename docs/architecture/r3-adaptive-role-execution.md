# R3 PR3c — Host-governed adaptive role execution (experimental)

**Architecture:** PR3-A–PR3-J owner-approved. **Prerequisites:** merged PR3a (#63) and PR3b (#64).
**Status:** Draft implementation; no owner-local GREEN or genuine Ollama acceptance yet.

## What is implemented

- **Separate model path:** Stable Gate C and structured R3 v1 questions continue using the unmodified legacy independent MAF runner. Only an explicitly admitted `r3-adaptive-question-context-v2` run selects `AdaptiveMafRoleStageRunner`. Both paths share the original Reviewer reconciliation contract.
- **Structured gap proposal:** An independent role first selects 1–3 of the existing four read-only capability views using the original bounded JSON schema. The host executes each request and commits its complete, fenced, role-scoped receipt **before** displaying it to the role's MAF agent. The model then produces exactly one schema-constrained gap proposal: `none` or one of four bounded gap kinds, plus a 1–600-character purpose. No target, path, arbitrary tool or command can be requested.
- **Host decision:** The host maps the approved gap kind to a fixed capability. A genuinely new capability can be requested **once**, its receipt and proposed/allowed/new-or-no-new gap events committed atomically before model delivery. An already-served capability is **denied without a second call**, recording proposed + denied events under the fence. `none` moves directly to assessment without additional retrieval; no fabricated gap event is added.
- **Budget and trust:** Maximum 3 initial views and one follow-up; at most 4 of the existing six possible call slots, 48 distinct items, 12 per view, 90 seconds per role and six minutes per attempt. Two bounded host-validated final assessment attempts remain. Source excerpts, comments and labels are untrusted data, never agent instructions or proof of runtime behavior.
- **Version-aware checkpoints:** The existing managed worker dispatches only v2-admitted runs to the adaptive runner. On completion, the coordinator independently reopens the saved source and **replays all committed receipts and gap events** before writing the new v3 checkpoint. Its digest, exact evidence mappings and call count must match. Reviewer gets v3 profile identity but no direct evidence receipts. Historical v2 checkpoints and v1 questions remain on the original validator, with explicit continuation still required on restart.
- **Explicit API & Atlas opt-in:** An authenticated, CSRF/origin-checked `POST .../investigations/questions/adaptive` verifies the saved HTTP candidate and commits the adaptive v2 question in the same fenced admission transaction. `GET .../investigations/{runId}/adaptive-context` exposes verified context. Atlas offers a separate **Investigate experimentally (adaptive)** action only for supported HTTP candidate kinds; stable investigation remains the primary button. The UI verifies the exact response version, question/run/snapshot/node identity before navigating to Observatory and Findings. Both profiles have distinct idempotency identities.
- **Denial replay:** Adaptive receipt revalidation now accepts exactly one duplicate proposal + denial pair if and only if its mapped capability was already served in the original initial batch. No new evidence handle or charge is fabricated. The writer checks the same SQLite transaction and current owner epoch.

## Test gates

All model-free checks are part of `scripts/check.ps1`:

- `--r3-adaptive-ledger-self-test` (PR3a/PR3b receipts, schema, historical migrations, tamper negative cases).
- `--r3-adaptive-execution-self-test` (PR3c): strict gap JSON parser; real managed-worker adaptive dispatch with fixture roles; persisted authorized follow-up and duplicate denial; role isolation; genuine C6b Reviewer; three accepted v3 checkpoints and exact replay; **manual restart after Skeptic timeout** that reuses only the accepted Investigator checkpoint.
- HTTP integration rejects invalid/unauthenticated/non-HTTP/foreign scope experimental admissions **before invoking Ollama**, and checks OpenAPI routes.
- Browser test performs real API pairing/capture/Atlas navigation with the stable and adaptive question POST responses intercepted to avoid actual model calls, and checks exact v1/v2 contracts, separate idempotency and no unintended admission on navigation.

Separate **genuine Ollama** acceptance (excluded from `check.ps1`):

```powershell
dotnet run --project .\tests\maf-adapter-contract\MafAdapterContractProbe.csproj --configuration Release -- --r3-adaptive-run 1
```

The probe uses a disposable real source capture and actual local MAF / `qwen3.5:9b`, executes the production managed worker and checks the terminal run, three v3 checkpoints, receipt/gap budgets, and full independent saved-evidence checkpoint replay. **Every requested trial must pass.** Model timeout, invalid structured output, or replay failure is RED, never silently discarded. It is a functional acceptance test, *not* evidence of improved investigative usefulness.

## Owner verification

```powershell
git fetch origin
git switch feature/r3-adaptive-role-execution-pr3c
git pull --ff-only
.\scripts\check.ps1
```

Only after full GREEN, run the separate genuine Ollama probe above. Do not merge without both checks and explicit owner approval. Stop manually running API/UI servers on ports 5127/5175 before the Playwright suite.

## Still needed for the complete PR3 milestone

- Scoped, readable gap/receipt outcome projection inside Mission Control, including historical interrupted runs and explicit absence of new evidence.
- Additional integrity fault-injection cases for changed or missing accepted v3 receipts and cancellation/deadline boundaries.
- Genuine Ollama validation for completion and measurable limitations under 90-second per-role budget.
- PR4 remains responsible for a **preregistered** comparison against fixed Gate C and deterministic baseline using unchanged case labels and zero integrity breaches. Do not claim semantic improvement from GREEN alone.

No new arbitrary source access, external provider, executable source code, new capability type, automated recovery, or cross-role evidence sharing is introduced.
