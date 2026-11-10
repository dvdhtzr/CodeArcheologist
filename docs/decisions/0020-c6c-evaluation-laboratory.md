# ADR 0020 — C6c hybrid evaluation laboratory

- **Date:** 2026-11-10
- **Status:** Decisions approved by the owner. Slice C6c-1 is in implementation; owner verification is pending.
- **Architecture:** Gate C v1 decisions 8–9 (`docs/architecture/gate-c-v1.md`); C6 decision D3 (hybrid laboratory)
- **Dependencies:** C6b (merged in PR #34); the existing Gate B, Gate C and evidence contracts

## Context

Gate C v1 already approved the shape of the laboratory:
- a 60-case corpus: 20 purpose-built fixtures, 15 curated real repositories, 15 deliberate change experiments and 10 held-out cases;
- 12 diagnostic cases repeated for two more trials, giving 84 Gate C evaluations;
- comparison of a deterministic baseline, Gate B and Gate C;
- zero-tolerance integrity blockers and provisional analytical thresholds.

The decisions below settle how this is done without the lab measuring itself.

## Decisions (approved by the owner on 2026-11-10)

1. **Labels.** The assistant drafts structured labels: required observations with source anchors, required uncertainties, forbidden claims, and material issues. The **owner reviews and signs** each label by committing `status: "owner-approved"`.
   - Labels are fixed before any agent output for that case is scored.
   - Held-out labels are authored by the owner alone. Only their SHA-256 seal is committed until scoring (C6c-3).
   - Draft labels may be used for *provisional* runs only. A scored acceptance run requires every label to be owner-approved.
2. **Usefulness.** The paired "actionable usefulness" metric (the ≥15-point gain over Gate B) is judged by the owner, **blinded**: anonymized, order-randomized pairs, with gate and model identity hidden until judging closes (C6c-3).
   - No LLM judge is used for any metric of record.
   - Integrity, citation validity, objection coverage and completion are scored deterministically.
3. **Real repositories.** A manifest pins each repository's URL, exact commit SHA and expected snapshot fingerprint.
   - A separate script, run by the owner outside the application, acquires them (C6c-2).
   - The lab captures them through the normal read-only snapshot path and fails closed on any fingerprint mismatch.
   - The application itself never invokes Git or fetches repositories.
4. **Slicing.**
   - **C6c-1:** case and label schema, corpus validator, isolated harness, versioned budget declaration, three comparators, deterministic metrics, 20 fixtures.
   - **C6c-2:** change experiments and curated real repositories.
   - **C6c-3:** blinded judging, held-out sealing, repeatability trials.
   - **C7** then performs acceptance.

## Invariants

- Every lab run uses its own disposable data directory. Fixture sources are copied to a temporary directory before capture, and the repository tree is never modified.
- No imported or fixture code is compiled or executed. Fixtures are written-syntax inputs only.
- The lab reuses production code paths (snapshot capture, evidence graph, `SnapshotEvidenceTools`, `GateBInvestigator`, the C6b worker and coordinator with `MafRoleStageRunner`). It adds no weaker validation path.
- The lab-config budget file is a **declaration checked against the host's constants**. A mismatch fails closed; the file never silently reconfigures the host.
- Label anchors are source locations chosen by the labeler. The validator checks that each anchor is citable in the analyzer's evidence package, so labels are satisfiable. This is a consistency check, not a derivation of expectations from analyzer output.
- Metrics that need judgment are reported as `pending-judgment` and are never estimated. These are material-issue detection and usefulness. A lexical "forbidden claim" screen is a review aid flagged for humans, never a score.
- Results keep their fixed denominators. Incomplete and inconclusive results stay visible.

## Addendum — C6c-2 curated repositories (approved 2026-11-10)

- **Repository list:** the owner approved 15 curated repositories, the new `expected-no-supported-candidate` case kind, and assistant-chosen entry points, which the owner reviews along with the labels.
- **Subtree scoping (approved):** 6 repositories exceed the analyzers' deliberate 250-file / 8 MiB C# limit.
  - Each such case captures one pinned subtree through the normal snapshot path instead.
  - Analyzer limits are unchanged, and callees outside the subtree are honestly unresolved.
- **Expectation kinds:**
  - `investigate`;
  - `no-supported-candidate`: zero supported HTTP candidates, nothing invented;
  - `capture-limit-fail-closed`: a 413 snapshot limit, and no snapshot record left behind.
- **Analyzer bug found by the survey and fixed:** `StaticCSharpProjectInspector` decoded `.csproj` bytes including the UTF-8 byte-order mark. The resulting U+FEFF made `XmlReader` reject ordinary Visual Studio projects, so 7 of the 15 real repositories failed closed. Exactly one leading BOM is now stripped; DTD/entity prohibition and all limits are unchanged. A regression test fails without the fix.
- **Reclassification:** R11 (aspnetrun, Carter) was proposed as an unsupported convention. Its modules still call `app.MapX`, so the survey found 17 supported candidates, and it is now a Minimal API case.
- **Ocelot:** scoped to the gateway-only `samples/Basic`, because the full `samples/` tree contains downstream services with ordinary endpoints.
- **Slicing:** C6c-2 is delivered as two PRs. C6c-2a covers the curated repositories; C6c-2b covers the 15 deliberate change experiments.
- **Portability (found by the owner's Windows run):** R04 Equinox contains both `Migrations/EventStoreSQL/` and `Migrations/EventStoreSql/`. A case-insensitive filesystem merges them, so the Windows snapshot differed.
  - Pinned curated subtrees must now be free of case-insensitive path collisions. The validator enforces this on the per-file manifests, and both acquisition scripts refuse such a subtree.
  - R04 is re-scoped to `src/Equinox.Services.Api`: same commit and entry point. The Register callee is now honestly unresolved, because the service interface lies outside the subtree.
- **Diagnostics:** per-file manifests (`eval/curated/manifests/<id>.tsv`) are pinned and must reproduce the fingerprint. A mismatch names the changed, missing or extra entries and is never accepted.

