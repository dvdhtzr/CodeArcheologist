# Evaluation Laboratory V2 — Approved Design and PR1 Boundary

Status: V2-D1, D2, D3 and D4 architecture approved by the repository owner.
Implementation status: **PR1, PR2, PR3a, PR3b and PR4a merged; PR4b independent judging under local verification**. This document
does not attest that a V2 corpus is semantically reviewed or ready for scoring.

## Why V2 exists

The historical R3 PR4 study remains frozen and inconclusive. Post-hoc review found
that some material issue labels required historical before/after information while
the compared model workflows investigated after-only source. V2 must independently
establish source truth, evidence availability, and answer quality before producing
a credible score. Historical C6c, C7, and PR4 artifacts, verdicts and labels remain
unchanged, and adaptive-agent expansion is frozen.

## Approved V2 task families

- **Current code:** answer from one pinned source snapshot.
- **Change impact:** answer from governed and authorized before/after evidence.
  Scored change-impact analysis is blocked until a production-supported evidence
  interface exists; an evaluator-only shortcut is not acceptable.
- **Evidence boundary:** correctly recognize unsupported conventions, unavailable
  evidence, and limits on conclusions.

Operational integrity and reliability are scored separately from code semantics.

## The three separate questions

1. **Source truth:** what does the pinned source actually establish?
2. **Answerability:** what evidence was initially available or retrievable under
   the comparator's authorized scope and budgets, and what was actually delivered?
3. **Answer quality:** did the report provide a correct, sufficiently grounded
   answer, with justified uncertainty and no critical unsupported assertions?

Truth, availability and judgment are independently versioned. An accepted source
hash is not proof of C# semantics; a saved receipt is not proof that the model saw
its payload; an approved draft label is not semantic proof.

## V2 schema identities

The isolated namespace `CodeArcheologist.EvaluationLab.V2` defines:

| Identity | Intended later responsibility |
|---|---|
| `v2-case-v1` | Developer question, partition, task type, source and scope |
| `v2-ground-truth-v1` | Atomic, source-anchored expectations and reviews |
| `v2-answerability-v1` | Criterion and comparator evidence-access classification |
| `v2-evidence-ledger-v1` | Actual host request, return and model delivery history |
| `v2-judgment-v1` | Criterion-level grade and source-grounding review |
| `v2-adjudication-v1` | Independent dispute review and append-only disposition |
| `v2-judgment-record-v1` | Finalized judging record bound to a rubric |
| `v2-experiment-manifest-v1` | Frozen case/protocol/execution identity |

The PR1 validator checks **structure only** for case and ground-truth documents,
including versions, IDs, declared source hashes, safe relative paths, task-specific
before/after metadata, source-proof reference consistency, and strict JSON decoding.
PR1 does not verify source truth or actual bytes. PR2 adds bounded copied-file
SHA-256 and source-span verification plus source-first review import, but cannot
prove semantic correctness, host snapshot identity, actual answerability,
model visibility, or statistical quality.
These are future PR2–PR7 gates.

## PR2 — source-first proof verification and independent review

PR2 adds optional owner-operated model-free commands for a *diagnostic* V2
corpus. The owner provides a separate, immutable-as-reviewed source-copy tree:

```text
<source-root>/
  <case-id>/
    after/<relative-path-from-case-json>
    before/<relative-path-from-case-json>  # change tasks only
```

Source preflight compares **actual file bytes** to the case's declared SHA-256
digests and verifies every claimed proof line range against decoded UTF-8 text.
Missing, modified, oversized (>128 KiB per file; >384 KiB per case), unsafe,
or undecodable files fail closed. The tool does **not** execute imported code,
run MSBuild, consult live repository paths, or silently truncate excerpts.

**Crucial limit:** the case's declared snapshot fingerprint and optional Git
commit identity are still *declarations*. PR2 does not certify that the copied
files constitute an authoritative host snapshot or effective compiler context.
PR3 will establish evidence accessibility/receipt correlation. Path checks
avoid known reparse-point traversal but are not an OS-enforced race-free
filesystem sandbox; use trusted owner-acquired frozen copies.

```powershell
dotnet run --project .\tests\evaluation-lab\EvaluationLab.csproj --configuration Release -- --v2-pr2-self-test
dotnet run --project .\tests\evaluation-lab\EvaluationLab.csproj --configuration Release -- --v2-source-preflight --root .\eval\v2 --sources <source-root>
dotnet run --project .\tests\evaluation-lab\EvaluationLab.csproj --configuration Release -- --v2-review-export --root .\eval\v2 --sources <source-root> --out <new-review-dir>
```

Review export writes one source-first packet per case, a SHA-256-bound
`manifest.json`, instructions and a sibling ZIP for manually uploading.
**No agent reports or comparator output** enter source review. Source text is
untrusted data; embedded instructions must be ignored. Source data may contain
private code, so the owner decides whether and where to upload the ZIP.

The independent reviewer returns a complete strict
`v2-source-review-answers-v1` JSON document with `exportSha256` equal to
the SHA-256 of the manifest bytes, `reviewerId` different from the author,
and every criterion assigned a source-specific verdict:
`supported`, `contradicted`, `insufficient-evidence`, `ambiguous`,
`wrong-task`, or `nonmaterial`. Every supported verdict needs an exact
packet proof reference, and every verdict needs a substantive explanation.
If evidence is insufficient, the reviewer must not guess.

```powershell
dotnet run --project .\tests\evaluation-lab\EvaluationLab.csproj --configuration Release -- --v2-review-import --export <review-dir> --answers <answers.json>
```

Import rejects incomplete/duplicate/foreign verdicts, malformed references,
manifest mismatch, altered source packets, self-review, and any attempt to
overwrite the first imported review record. This is **a source-review receipt**,
not proof that the reviewer is genuinely independent or that the judgment is
semantically correct. It does not alter ground-truth labels or set review status.
Disputes, owner approvals, answerability, evidence delivery and scored judging
belong to later gates. All PR2 outputs say **NOT READY FOR SCORING**.

## PR1 command surface

From the repository root:

```powershell
dotnet run --project .\tests\evaluation-lab\EvaluationLab.csproj --configuration Release -- --v2-self-test
dotnet run --project .\tests\evaluation-lab\EvaluationLab.csproj --configuration Release -- --v2-validate --root .\eval\v2
dotnet run --project .\tests\evaluation-lab\EvaluationLab.csproj --configuration Release -- --r3-pr4-validate
.\scripts\check.ps1
```

`--v2-validate` reports the number of structurally valid cases and explicitly
says **NOT READY FOR SCORING**, even if every structural check passes. An empty
V2 corpus is permitted at PR1; no scored study is performed.

`--v2-self-test` uses synthetic, disposable inputs and needs no Ollama.
Neither command reads a user's real workspaces or executes imported repositories.

## Approved implementation roadmap

1. **PR1:** isolated structural contracts, parser/validator and negative tests.
2. **PR2:** independent source-first ground-truth authoring and validation.
3. **PR3:** per-comparator answerability preflight and actual evidence delivery
   attribution, reusing authoritative production evidence and checkpoint contracts.
4. **PR4:** calibrated source-grounded batch judging, blinding and adjudication.
5. **PR5:** supported-task-success scoring, sealed census and cluster-aware reporting.
6. **PR6:** diagnostic qualification with model-free and real Ollama checks.
7. **PR7:** a protected held-out corpus (~60 cases, ~20 independent repository
   families), frozen manifests and scored-study preflight.

First scored study: estimate the existing fixed Gate C workflow's absolute ability
to produce supported developer answers. Adaptive comparison is a **separate future
study** requiring its own held-out corpus, statistical power justification and
explicit approval. Historical 50 cases are for diagnostic development only.

## Safety and governance

The historical C6c/C7/PR4 validator and reports are not changed by PR1.
All project safety constraints in `AGENTS.md` continue to apply: verify exact
source identity, do not execute imported source or project builds, preserve
typed and immutable evidence, work in disposable isolated data directories.

The complete Windows `scripts/check.ps1` verification and owner-local GREEN
are mandatory before merging. Real Ollama regression is separate. This project
does not use GitHub CI as a substitute for local verification.

## PR3a — before-outcome host answerability preflight (diagnostic only)

PR3a creates an isolated, deterministic evidence **retrievability preflight** for
fixed Gate C Investigator or Skeptic, before any agent output is examined.

Inputs (all are owner-supplied, and must come from frozen source-first review):

- The V2 diagnostic case directory and ground-truth document (PR1 schema).
- Owner-acquired `<sources>/<case-id>/after/<relative file>` (PR2 verified).
- The frozen PR2 `review-import.json` and its original manifest, source packet
  and instruction files. All relevant source criteria must have independently
  reviewed **supported** verdicts.
- A strict `v2-answerability-plan-v1` with `caseId`, `comparator:
  "gate-c"`, `role: "investigator" | "skeptic"`, an `entryKind`,
  `entryPath`, `entryLine`, `sourceReviewImportSha256` and an
  explicit `bindings` array of `criterionId`, `evidenceId` and
  zero-based `proofIndex`. The plan must cover every required evidence ID;
  unknown/incomplete mappings cannot establish positive availability.
  The input case itself must pin the *actual host snapshot fingerprint*.

Example **synthetic mapping shape only**, not a ready-to-run case:

```json
{
  "schemaVersion": "v2-answerability-plan-v1",
  "caseId": "V2-example-diagnostic",
  "comparator": "gate-c",
  "role": "investigator",
  "entryKind": "http-minimal-mapping",
  "entryPath": "Api/Routes.cs",
  "entryLine": 12,
  "sourceReviewImportSha256": "REPLACE_WITH_ACTUAL_LOWERCASE_64_HEX_SHA256",
  "bindings": [{"criterionId":"OBS-01","evidenceId":"EVD-01","proofIndex":0}]
}
```

```powershell
dotnet run --project .\tests\evaluation-lab\EvaluationLab.csproj --configuration Release -- --v2-answerability-self-test
dotnet run --project .\tests\evaluation-lab\EvaluationLab.csproj --configuration Release -- --v2-answerability-preflight --root <v2-root> --sources <owner-source-root> --review <PR2-review-dir> --plan <plan.json> --out <new-preflight.json>
```

**Mechanism:** The evaluator recaptures the owner's read-only after-source
through the trusted local snapshot host into disposable data, requires the
captured fingerprint to exactly match the V2 case, and resolves an exact
HTTP-entry node. It opens *fresh, isolated Gate C evidence sessions* and
deterministically probes all combinations of 1–3 registered capabilities
without invoking Ollama. A positive `retrievable` result requires a single
allowed combination to expose **every** required after-snapshot source anchor
as an observed-syntax, SHA-256-matching, role-local evidence link, within
existing view/tool/item and model-input-size limits. No handles from different
sessions may be combined. The original source, approved reviews and historical
labels remain untouched.

A failure to find matching evidence is **undetermined**, not automatically
`unavailable`; evidence can exist in excerpts or other context even when a
specific proof anchor is not found. Boundary and negative criteria also remain
undetermined pending source-specific interpretation. No `initially-available`
claim is currently made for fixed Gate C because it first asks the model to
choose capabilities. Nothing in PR3a proves what any particular agent selected,
requested, saw or processed.

The output is one non-overwriting, locally created
`v2-answerability-preflight-record-v1` with exact case, ground truth, reviewed
receipt, plan, host fingerprint, contract identities and computed classifications,
plus a digest binding the classifications and inputs. **It is not a
model-delivery ledger or a scored verdict.** An independent actor can still
forge local JSON or an external reviewer ID; digest and filename checks are
corruption detectors, not signatures/authentication. PR3b will review and
instrument real provider-adapter handoff boundaries separately. No provider
call, production checkpoint, agent prompt or budget is changed by PR3a.

**Safety:** This diagnostic workflow operates on owner-acquired source copies
and uses the existing trusted snapshot capture services. It does not execute
imported source or call an external model. It uses process-local, disposable
workspace state; do not run it concurrently with another in-process lab capture.
It never writes to the original source copies or frozen review input directory.
No held-out or historical change-impact cases are eligible at PR3a. Passing
`check.ps1` does not certify a real corpus's answerability; the synthetic
self-tests verify the contracts while case-level preflight must be run separately.
All outputs remain **NOT READY FOR SCORING**.

## PR3b — observed model handoff (opt-in diagnostic)

PR3b adds an isolated, fenced v9 model-handoff observation ledger, without
modifying existing R3 adaptive receipt authority or checkpoint formats.
The optional diagnostic records `prepared`, `adapter-entered`,
`completed` and `faulted` as distinct host events.

**Prepared is not invoked. Entered is not proof the local provider received
or understood any source bytes.** Adaptive handoffs reference existing
fenced receipt IDs, and the fixed path records only authorized host evidence
links. Reviewer handoffs remain derived-assessment-only. No raw prompt or
provider response is persisted.

Run `--v2-handoff-self-test` during the full local `check.ps1`, followed
by one real Ollama trial `--v2-handoff-run 1` after local GREEN. The
historical `--r3-adaptive-run` remains unmodified and unobserved.
Both paths are diagnostic only. No scored experiment is authorized.

See `docs/architecture/v2-pr3b-model-handoff.md` for the exact authority
boundary, failure handling, live command and remaining limitations.

## PR4a — source-first blind diagnostic packets

A frozen, source-grounded rubric and `v2-pr4a-export-plan-v1` support
model-free anonymous packet exports for owner-supplied diagnostic reports.
The plan pins source-review import, rubric and exact report hashes. The
exporter re-verifies the PR2 source-review manifest and all selected supported
criteria, checks source copies and exact report case/snapshot identity,
refuses held-out/change-impact inputs, and outputs shareable anonymous
packets plus a private non-uploadable comparator/claim map. It preserves
noncompleted outcomes and untrusted, potentially forged model citations.
It does not judge, calibrate, adjudicate, or score.

Crucial: current PR4a report attribution is owner-supplied, **not
host-attested**. The merged PR3b smoke probe did not preserve a
V2-scored report artifact. PR4a does not fill that gap by assertion.
PR5/PR6 must qualify genuine report-to-handoff provenance before scoring.

See `docs/architecture/v2-pr4a-blind-judging.md` for input schemas,
CLI commands, privacy limits, tests and PR4b handoff.

## PR4b — owner-operated independent judge imports (diagnostic)

PR4b extends the merged PR4a blind export with a **separate, offline**
mixed calibration/diagnostic batch: `--v2-pr4b-prepare`, 
`--v2-pr4b-import`, and `--v2-pr4b-audit` plus model-free
`--v2-pr4b-self-test`. A frozen private calibration key is checked
against disjoint PR4a source-reviewed calibration packets, then
hash-shuffled together with target diagnostics. The full public ZIP is
re-hashed; it never contains labels or comparator/private run mappings.

Two distinct judge/session identities are required for the two complete
first-write import slots. They are **owner declarations**, not verified
human/LLM identities. Each packet requires every expected criterion and
every model claim to be judged, including unanticipated unsupported
claims. Imported judgments retain exact answer-file hashes, a whole-record
digest and calibration agreement/disagreements. Import is atomic and
non-overwriting. Neither calibration nor a completed report is
automatically qualified for scoring.

The application makes **no** external model calls. The owner is responsible
for source-sharing authorization and running separate judging sessions.
No adjudication, statistical scoring or ground-truth promotion is
implemented. PR4c and PR5–PR7 retain those responsibilities. Source
review and actual provider-output attribution remain distinct.

See `docs/architecture/v2-pr4b-independent-judging.md` for contracts,
owner-operated flow, risks and acceptance gates.
