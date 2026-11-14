# V2 PR4a — Source-Grounded Blind Export (design/implementation boundary)

Status: draft implementation, local GREEN pending. PR4a only. PR4b and
PR4c are **not implemented**. Historical C6c/C7/R3-PR4 labels and judgments
are frozen and unmodified. **NOT READY FOR SCORING.**

## Purpose

Prepare one immutable-by-first-write, source-first blinded packet per
**diagnostic** V2 case for later independent external judgment. This stage
does NOT call an external judge, ask scoring questions, import answers,
create adjudications, evaluate a model, or compute performance metrics.

Source proof and semantic judgment are different things. A SHA-256-matching
source line and a valid model citation do **not** prove the assertion stated
by a model. The PR4a rubric explicitly preserves not-demonstrated cases,
required uncertainty and unexpected false assertions.

## Trust boundary and its deliberately unclosed gap

The input report uses the isolated `v2-pr4a-diagnostic-report-v1` JSON
schema. Its `attribution` must be exactly
`owner-supplied-not-host-attested`. PR4a checks the **exact file bytes**
against a pinned input SHA-256, its declared case/snapshot identity, each
claim and the declared outcome. The report is nevertheless an owner-supplied
diagnostic input, **NOT an independently authenticated export of the live
Gate C model result**. Its run/attempt IDs are declarations, not evidence
that a specific provider call actually produced those bytes.

Consequently, a successful export does not certify report origin, receipt
sufficiency, semantic accuracy, or suitability for a scored experiment.
A subsequent qualification gate must supply a trusted frozen host report
and its actual PR3 handoff linkage before any scored study. Do not treat
PR3a's retrievability or PR3b's model-call counters as a substitute.

PR4a validates the PR2 source-first manifest, frozen proof packet and
complete supported source-review verdicts against the actual V2 source
copy bytes. The source-copy fingerprints printed by PR2 remain declared
until independently verified by host capture; a matching file digest is
not an assertion of compiler or runtime semantics.

## Input layout

Owner prepares diagnostic inputs, outside committed production data:

```text
<root>/
  cases/<case-id>/case.json
  cases/<case-id>/ground-truth.json
<sources>/<case-id>/after/<authorized-source-path>
<review>/manifest.json
<review>/review-import.json
<review>/INSTRUCTIONS.txt
<review>/<case-id>.txt
<reports>/<case-id>.report.json
<plan>.json
<rubric>.txt
```

The V2 corpus can be a **subset of diagnostic cases only**; the export plan
must explicitly enumerate the cases being reviewed and can never claim a
complete scored census. Held-out and before/after change-impact records
are rejected at PR4a. Imported repository code is never executed or built.

Example **shape only**, not a valid ready-to-run plan (replace all digests):

```json
{
  "schemaVersion": "v2-pr4a-export-plan-v1",
  "rubricSha256": "REPLACE_WITH_REAL_64_LOWERCASE_HEX_SHA256",
  "reviewImportSha256": "REPLACE_WITH_REAL_64_LOWERCASE_HEX_SHA256",
  "items": [
    {
      "caseId": "V2-example-source",
      "reportFile": "V2-example-source.report.json",
      "reportSha256": "REPLACE_WITH_REAL_64_LOWERCASE_HEX_SHA256"
    }
  ]
}
```

A sample `<reports>/<case-id>.report.json` shape:

```json
{
  "schemaVersion": "v2-pr4a-diagnostic-report-v1",
  "caseId": "V2-example-source",
  "comparator": "gate-c",
  "runId": "00000000-0000-0000-0000-000000000001",
  "attemptId": "00000000-0000-0000-0000-000000000002",
  "snapshotFingerprint": "REPLACE_WITH_CASE_AFTER_SNAPSHOT_SHA256",
  "attribution": "owner-supplied-not-host-attested",
  "outcome": "completed",
  "claims": [
    {
      "id": "RAW-01",
      "kind": "observation",
      "text": "What the model actually claimed (do not polish or paraphrase).",
      "citations": [
        {"filePath": "src/File.cs", "fileSha256": "REPLACE_WITH_SOURCE_FILE_SHA256", "line": 10}
      ]
    }
  ]
}
```

`outcome` may be `completed`, `failed`, `timed-out`, `cancelled`,
`incomplete` or `not-produced`. A completed report must contain
claims; a not-produced report must have zero claims. Other partial outcomes
retain original claims, but the packet conspicuously marks them
**NONCOMPLETED**. Invalid model citations are retained as untrusted
assertions to be judged later, not silently discarded as input errors.

Run from the trusted repository root:

```powershell
dotnet run --project .\tests\evaluation-lab\EvaluationLab.csproj --configuration Release -- --v2-pr4a-self-test

dotnet run --project .\tests\evaluation-lab\EvaluationLab.csproj --configuration Release -- --v2-pr4a-export --root <v2-root> --sources <frozen-source-root> --review <PR2-review-folder> --reports <reports-folder> --plan <plan-file> --rubric .\eval\v2\source-grounded-rubric-v1.txt --out <new-export-folder>
```

## Output / strict ownership

The exporter validates all inputs and size budgets *before* creating the
output folder. It refuses existing or overlapping output destinations and
never overwrites a prior packet. A failed write may leave an incomplete
output directory, which is never treated as successful. New exports must
use a new folder, not repair/replace a prior evaluation.

```text
<out>/
  DO-NOT-UPLOAD-private-key.json
  upload-these-to-judge/
    INSTRUCTIONS.txt
    packet-001.txt
    packet-002.txt
    ...
  upload-these-to-judge.zip
```

Only `upload-these-to-judge.zip` or the content of
`upload-these-to-judge/` is intended for an external judge.
**NEVER UPLOAD THE PRIVATE KEY.** It maps anonymous IDs back to
comparator, run, case and original claim identities, and binds exact
SHA-256 digests for every input, packet and the frozen rubric.
A judge cannot infer authorization from a report's citation alone.
The complete original source-first PR2 source packet is included;
owner must explicitly approve sharing each source copy with ChatGPT.

The rubric is frozen by exact input-plan digest. Packet content is not
truncated or silently repaired; oversize is an export failure. Source
packets and reports may contain malicious instructions, so judges must
treat both as untrusted data. Blinding removes explicit comparator/run
metadata, but report voice or wording could still reveal its origin.

## Intended PR4b / PR4c handoffs

PR4b must verify the private key and packet hashes again before accepting
exactly one complete, separately recorded judgment per independent judge.
It must pre-calibrate judges using independently sourced examples; old
C6c left/right guesses are **not calibration truth**. Owner-operated
external ChatGPT is allowed by explicit V2 policy, but two separate
sessions are not independently trained or guaranteed independent;
publish this limitation. No silent grade promotion from source citation.

PR4c must identify **every** disputed material criterion and every
unexpected potentially unsupported claim; preserve both original
judgments and write new, append-only adjudication decisions. Unresolved
means unresolved. PR5, not PR4, defines the scored census and statistics.

## PR4a merge gate

- Full owner-local `.\scripts\check.ps1` GREEN.
- `--v2-pr4a-self-test` GREEN (real PR2 export/import fixture and negative cases).
- Review private/public packet boundaries, digest attribution, complete
  source/claim inclusion, partial outcomes and input-overwrite guards.
- Explicit owner approval before merging.
