# Evaluation Laboratory V2

**PR1, PR2, PR3a, PR3b and PR4a merged; PR4b offline judging under review. NOT READY FOR SCORING.**

This directory is a separate V2 input namespace. Nothing here replaces the
historical `eval/cases`, `eval/lab-config.json`, C6c/C7 labels, or frozen PR4
reports and judgments.

Proposed V2 case layout (future source-first validation in PR2):

```text
eval/v2/
  README.md
  cases/
    V2-example-case/
      case.json
      ground-truth.json
```

PR1 deliberately supplies **no real case records**. The validator accepts a
missing/empty `cases` directory, prints `0 cases`, and always reports
`NOT READY FOR SCORING`.

From the repository root:

```powershell
dotnet run --project .\tests\evaluation-lab\EvaluationLab.csproj --configuration Release -- --v2-self-test
dotnet run --project .\tests\evaluation-lab\EvaluationLab.csproj --configuration Release -- --v2-validate --root .\eval\v2
```

`--v2-validate` checks the structure of each case/ground-truth pair: schema
identities, required fields, source hash shapes, safe relative paths,
criteria identity, task compatibility and source-proof reference consistency.
It does **not** establish source truth or independently validate label meaning.

The tests use disposable synthetic sources and execute no imported project.

See `docs/architecture/v2-evaluation-laboratory.md` for the approved D1–D4
architecture, future judging and evidence requirements, and the PR1 boundary.

## PR2: pinned source copies and owner-operated review

PR2 adds `--v2-source-preflight`, `--v2-review-export`,
`--v2-review-import` and `--v2-pr2-self-test`.

The V2 diagnostic case layout remains separate from historical evaluation
files. Each case additionally needs a trusted owner-acquired **source copy**
at `<source-root>/<case-id>/after/<declared-file>`; change tasks must also
include `before/<declared-file>`. Preflight hashes actual file bytes, checks
bounded UTF-8 line spans and rejects missing or modified sources. It does not
verify the declared snapshot fingerprint as a host-authoritative identity.

Review export creates a source-only ZIP and manifest for one owner-operated,
independent review. No agent output is exported. Review import binds every
criterion verdict to that manifest, refuses overwrite, and records review
without changing a label or authorizing scoring.

PR2 contains no committed diagnostic cases, no real Ollama runs, and no
automatic external model calls. The implementation is subject to local
Windows GREEN and owner merge approval.

See `docs/architecture/v2-evaluation-laboratory.md` for command examples
and acceptance boundaries.

## PR3a: Host-probed answerability, no model

New diagnostic commands are `--v2-answerability-self-test` and
`--v2-answerability-preflight --root <dir> --sources <dir> --review <dir>
--plan <file> --out <new-file>`. A plan binds reviewed atomic criteria
and required evidence IDs to proof indices and a fixed Gate C entry selector.
The output is immutable-on-first-write, diagnostic, and never scoreable.

The probe re-captures pinned files into disposable host state and checks exact
snapshot fingerprint plus real source evidence link digests and lines. It
tests permissible 1–3 capability combinations within the fixed host budgets.
It never observes or simulates an agent's actual selection or provider delivery.
No matching host view means **undetermined**, not `unavailable`. Source and
review files remain read-only; output paths within input roots are refused.
See the architecture document for safeguards and an input shape.

## PR4a: Frozen rubric and anonymous source-grounded packets

`--v2-pr4a-self-test` uses a disposable synthetic source, genuine PR2
export/import, an owner-supplied diagnostic report and tampering cases.
`--v2-pr4a-export` accepts explicit `--root`, `--sources`,
`--review`, `--reports`, `--plan`, `--rubric` and `--out`.

It creates `upload-these-to-judge.zip` containing only anonymized
source-first packets and instructions, plus a separate
`DO-NOT-UPLOAD-private-key.json` binding report, case, source and
rubric hashes to anonymous claim IDs. Do not upload the private key.
Source code and reports may be confidential; explicitly choose what
you send to an external judge.

PR4a deliberately does NOT import judgments, assess calibration, or
compute scores. Its owner-supplied report format is **not a genuine
provider-output attestation**, even when its SHA-256 matches. See
`docs/architecture/v2-pr4a-blind-judging.md` for this limitation,
the exact input schema, example plan and command.

## PR4b: Unmarked calibration and two independent judging imports

The owner can create a mixed batch using two already-frozen, disjoint PR4a
diagnostic exports and a pre-written owner/independent-reviewer calibration
key. Only `upload-these-to-judge.zip` may be shared externally.
The private key binds source, rubric, report, calibration expectations,
packet IDs and original claim IDs.

`--v2-pr4b-prepare` makes the mixed blind archive,
`--v2-pr4b-import` records one complete judgment for slot A or B, and
`--v2-pr4b-audit` verifies both, including all calibration disagreements,
full report-claim coverage and ZIP membership. No model is called in this
application. Each judging pass must be run independently by the owner.

The calibration bank uses **new independent source-reviewed cases**,
not historical C6c left/right guesses. Two ChatGPT sessions are not
statistically independent and the identity fields are not authenticated.
The imported diagnostic reports remain owner-supplied, not provider-attested.
Passing the calibration test does not establish scoring eligibility.

See `docs/architecture/v2-pr4b-independent-judging.md`.

## PR4c — source-grounded dispute and adjudication audit

`--v2-pr4c-export --batch <PR4b-batch> --out <new-export>` verifies the
two complete PR4b original judgments, computes every criterion/claim
difference and publishes anonymous source-first dispute packets. Its
private census records the original judge orientation, exact A/B
record SHA-256, calibration discrepancies, and unreviewed agreed
critical claims. Only `upload-these-to-adjudicator.zip` may be shared.

`--v2-pr4c-import --batch <PR4b-batch> --export <PR4c-export> --answers <file>`
first-write imports a complete adjudication with confirmed/revised/
unresolved dispositions for every dispute, strict authorized source
references and no overwritten judge records.

`--v2-pr4c-audit --batch <PR4b-batch> --export <PR4c-export>`
independently rederives the dispute census and checks all source packet,
ZIP, original judge and adjudication record digests. Model-free
`--v2-pr4c-self-test` is included in the full local check gate.
All V2 diagnostic results remain **NOT READY FOR SCORING**.

See `docs/architecture/v2-pr4c-adjudication.md` for the full protocol.

## PR5a — inspect actual persisted host report (read-only, private)

The `--v2-pr5a-export` command reads an **existing SQLite v9** database
without migrations or writes. It outputs the host's exact stored
`GateC5Report` text plus a private identity/digest receipt for
source snapshot, attempts, checkpoint lineage and PR3b model-adapter
events. This avoids replacing the original report with an
owner-transcribed PR4a diagnostic report.

```powershell
dotnet run --project .\tests\evaluation-lab\EvaluationLab.csproj --configuration Release -- --v2-pr5a-self-test
dotnet run --project .\tests\evaluation-lab\EvaluationLab.csproj --configuration Release -- --v2-pr5a-export --database <existing-workspaces.sqlite3> --run <guid> --out <new-private-folder-outside-DB-data-root>
```

Output (PRIVATE, not uploaded to external judges):

```text
<out>/
  PRIVATE-host-stored-report.json
  PRIVATE-host-provenance.json
```

If no report exists, only the provenance file is written, with explicit
blockers. Report and checkpoint hashes detect altered local data, not
adversarial forgery or semantic truth. PR3b adapter entry is not Ollama
acknowledgment; the exact **raw model response** was not retained by PR3b.
Host cautions are never model claims. PR5a does not produce public PR4a
judging packets, score actual developer answers, or change production.
All results remain **NOT READY FOR SCORING**.

See `docs/architecture/original-product-path-checkpoint-pr5a.md`
for the original vision, scope cap and mandatory post-PR5a owner-observed
developer-facing workflow before PR5b/PR5c work is prioritized.
