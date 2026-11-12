# R3 PR3 — Gap-Directed Evidence, Implementation Slice 1 (Foundations)

**Architecture approved:** PR3-A–PR3-J.
**State:** Draft; **not owner-verified**. No complete adaptive Ollama workflow is claimed by this slice.
**Base:** merged R3 PR2 (#62). No changes to `main`.

## Why separate the first GREEN gate?

R3 introduces new evidence lifecycle and crash-recovery invariants. The first slice establishes the deterministic storage foundation before adding further model calls and a checkpoint v3 reader. This avoids debugging a schema migration, MAF structured outputs and role-resume behavior simultaneously.

## Included in this slice

1. **Correct E10–E48 link ordering.** `GateCEvidenceSession.ReportLinks` returns numeric handle order, matching `RestoreHandles` expectations. Existing v2 checkpoints with fewer than ten handles are unchanged. New model-free test mints E1–E12, rehydrates a fresh session, checks exact link order and rejects cross-role restoration.
2. **Adaptive question context v2.** `r3-adaptive-question-context-v2` is independent of the existing R3 v1 question context and the original Gate C report. It preserves the exact immutable `GateCScope`, host-generated distinct question/run identities, approved HTTP-candidate kind, fixed question type, versioned template, experimental capability policy, budget profile and a digest. A separate atomic admission parameter permits model-free test fixtures to create this context. **The existing production API is unchanged**; it does not yet expose adaptive admission.
3. **SQLite v8 additive migration.** `adaptive_question_contexts` (v2 only), `adaptive_evidence_receipts` and `adaptive_gap_events` are separate append-only tables. Receipts and gap events have foreign keys to admitted adaptive v2 contexts. Their UPDATE/DELETE triggers forbid rewrites. Legacy events, attempts, v1 contexts, checkpoint tables and recorded reports are not rewritten.
4. **Fenced receipt writer.** The host-only storage operation checks the current attempt owner and epoch in its own immediate SQLite transaction. It verifies the exact saved run/scope and the last response issued by the role-specific evidence session, plus the valid catalog and call budget. Each accepted request writes a complete bounded response, exact role handle mapping, digest, ordinal and resource count. An allowed follow-up additionally records proposed, allowed and outcome gap events in the *same transaction*. The writer returns only after commit so an eventual adaptive runner can withhold evidence from the model until persistence is durable.
5. **Model-free tests.** `--r3-adaptive-ledger-self-test` checks v8 schema and v7 migration, v2/v1 separation, replay/conflict isolation, in-process fenced receipt admission, duplicate rejection, independent role numbering, no-new-evidence accounting and append-only triggers. `scripts/check.ps1` includes the probe and existing C5/C6b/R3 v1 migration assertions now require v8.

## Guarantees and non-guarantees

- These tables and APIs are **internal-only** in this slice. Production continues through the original Gate C default path.
- A row's SHA-256 digest detects accidental mutation. It is not an authentication MAC against a database administrator.
- A committed receipt does not establish the semantic value of evidence; the C6b model never sees or references these records yet.
- The host-only writer requires the caller to commit an accepted role result before showing it to the model; this will be enforced in the adaptive orchestration slice.
- A model cannot directly call the store; only the host can request a capability and persist results.
- Any future adaptive checkpoint must include and verify the full ordered receipt sequence **against a freshly verified saved snapshot**. Hash comparison alone is insufficient.
- Do not bump `GateCEvidenceRegistry.Version` or `GateC6CheckpointContract.Version` globally: doing so would invalidate historical v1/v2 checkpoints. The later version-aware dispatcher and revalidator must preserve both paths.

## Remaining PR3 work (not implemented here)

- Strict structured gap proposal schema and host-denied gap events.
- Host-governed initial and one-follow-up evidence acquisition in each independent MAF role, within the 90s/6 calls/48 items limits.
- Separate experimental admission API and explicit UI opt-in; stable PR2 user journey remains the default.
- Adaptive v3 checkpoint with committed ordered receipt sequence digest, reconstruction from verified source, exact fail-closed revalidation and migration-safe explicit continuation.
- Host-visible scoped gap projection, interruption/fence fault injections and real local Ollama acceptance.

These are required for the **PR3 milestone**, not optional polish. The next implementation slice is blocked on the initial local build and model-free GREEN for this schema/evidence foundation, followed by explicit owner merge approval. R3 PR4 comparative evaluations follow only after complete PR3.

## Local verification

```powershell
git fetch origin
git switch feature/r3-gap-directed-evidence-pr3
git pull --ff-only
.\scripts\check.ps1
```

When RED, share the first compiler/test error. The same branch stays draft until all foundation tests pass; a future PR will deliver the runtime adaptive protocol and checkpoint compatibility.

No source repository is executed and no real Ollama call is included in the new model-free tests.
