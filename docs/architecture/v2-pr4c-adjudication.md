# V2 PR4c — Deterministic Disputes and Source-Grounded Adjudication

**Status:** draft implementation; owner-local GREEN pending.
**Scope:** V2 evaluation-only diagnostic. **NOT READY FOR SCORING.**

This milestone extends PR4a's blinded owner-supplied diagnostic reports
and PR4b's two archived, separately imported judgments. It is not a
new truth source, not score estimation, and not an assertion of actual
provider-output authenticity.

## Trust boundaries

PR4c reads the original *unchanged* PR4b batch directory and requires
`--v2-pr4b-audit` to pass before doing any comparison. Its comparison
never trusts a judge's citation as semantic proof. The independent PR2
source review was already bound into the PR4b batch and every exact
source packet and ZIP member is revalidated.

All source text, report content, judge reasoning and adjudicator reasoning
remain **UNTRUSTED DATA**. The application makes no Ollama/ChatGPT API
calls, executes no imported code, and never modifies PR4b judge A/B files,
source copies, historical C6c/R3 labels, production prompts, evidence
tools, checkpoints, or worker state.

A/B are owner-declared separate judge identities, not cryptographically
authenticated independent minds. Source file SHA-256 and JSON record
digests detect drift, but cannot certify source semantics, third-party
identity, or a dishonest local filesystem administrator.

## Four distinct outputs

1. **Deterministic dispute census:** For *every* criterion, compare numeric
   grade (`null` distinct from 0), grounding, canonical proof-ID set and
   report-claim-ID set. For *every* report claim, compare grounding,
   materiality and canonical proof-ID set. Display order of references is
   immaterial. Every difference is recorded exactly once, with stable
   digest-derived dispute ID bound to frozen batch/A/B/packet/item/dimensions.
2. **Reason-variation notice:** Judge prose differences are preserved as
   `unclassified-reason-variance-not-automatically-semantic`. Lexical
   differences are *not* automatically declared substantive contradictions.
3. **Critical-consensus review flag:** Both judges marking the same claim
   `critical` and assigning the same grounding remains
   `unreviewed-critical-consensus-not-source-truth` even if it creates
   no dispute. Agreement does not equal independent source confirmation.
4. **Separate calibration audit:** Original A/B calibration differences
   and private official/calibration packet origins remain in the private
   census. The external adjudicator sees neither. Adjudication cannot
   re-label a frozen calibration answer; if source support is disputed,
   preserve an unresolved reason and refer for separately governed PR2
   source-label reconsideration. Never erase a calibration failure.

## Frozen source-first export

```powershell
dotnet run --project .\tests\evaluation-lab\EvaluationLab.csproj --configuration Release -- --v2-pr4c-export --batch <PR4b-batch> --out <new-export-dir>
```

Output:

```text
<new-export-dir>/
  DO-NOT-UPLOAD-pr4c-private-census.json    # includes source/judge mapping and calibration identities
  upload-these-to-adjudicator/
    INSTRUCTIONS.txt
    adjudicate-BPK-001.txt
    adjudicate-BPK-002.txt
    ...
  upload-these-to-adjudicator.zip             # ONLY externally share this ZIP
```

Only packets with one or more actual disputes are exported. No disputes
means the public ZIP contains just instructions and the private census
records zero disputes; there is still **no automatic semantic pass**.
The adjudicator source-first packet includes the already-frozen PR4b
source packet, the exact disputed criterion/claim, permissible source
proofs and report claim refs, and the two original opinions renamed
**First** and **Second**. Presentation order is deterministically
pseudo-shuffled from the frozen batch and judge-record digests. The
original judge slot mapping and calibration origin stay private.

The owner explicitly decides whether to share source code or report text
externally. Never upload the private PR4b/PR4c keys, calibration labels,
or the original judge records. Bounded source packets are never
truncated. Oversize, scope drift, missing source review or modified
original A/B judgments fails closed.

## Full third-party adjudication import

The owner passes the SHA-256 of the **private PR4c census file bytes**
as the public `censusSha256`; not the private file itself:

```powershell
(Get-FileHash -Algorithm SHA256 -LiteralPath '<new-export-dir>\DO-NOT-UPLOAD-pr4c-private-census.json').Hash.ToLowerInvariant()
```

The third adjudicator works from public source-first packets, with no
access to the private judge-slot/calibration crosswalk or original
judges' sessions. It returns **one complete JSON** matching
`v2-pr4c-adjudicator-submission-v1`:

```json
{
  "schemaVersion": "v2-pr4c-adjudicator-submission-v1",
  "censusSha256": "REPLACE_WITH_REAL_LOWERCASE_SHA256",
  "reviewerId": "source-adjudicator-1",
  "sessionId": "separate-session-1",
  "decisions": [
    {
      "disputeId": "DSP-REPLACE_WITH_EXACT_DISPUTE_ID",
      "disposition": "confirmed",
      "resolutionSource": "first",
      "grade": 2,
      "grounding": "supported",
      "materiality": null,
      "sourceProofRefs": ["OBS-01:P1"],
      "reportClaimRefs": ["CLM-001"],
      "explanation": "The frozen source supports exactly the first opinion, rather than the second."
    }
  ]
}
```

This is a *shape*, not a valid answer to any actual dispute.
Actual `grade`, `grounding`, `materiality`, `sourceProofRefs` and
`reportClaimRefs` must match the item's type and source scope.

Three disposition rules are enforced:

- `confirmed`: `resolutionSource` is `first` or `second` and the
  judge MUST preserve that entire selected conclusion (including exact
  canonical proof/claim reference sets). Requires a real referenced source
  proof and reason, but no judgment is automatically deemed correct.
- `revised`: `resolutionSource` is `independent`, with explicitly
  typed grade/grounding/materiality fields and source references. A grade
  of 3 cannot be granted to an unsupported conclusion.
- `unresolved`: `resolutionSource` is `none`. `grade`, `grounding`,
  `materiality` are all null; no report-claim links are promoted.
  Authorized source proof references may be listed to explain why
  evidence was insufficient. A substantive explanation is mandatory.

A third judge cannot provide foreign proof IDs, omit a dispute,
duplicate an ID, silently replace an A/B answer under `confirmed`,
or overwrite an already accepted answer.

```powershell
dotnet run --project .\tests\evaluation-lab\EvaluationLab.csproj --configuration Release -- --v2-pr4c-import --batch <PR4b-batch> --export <PR4c-export> --answers <adjudicator.json>

dotnet run --project .\tests\evaluation-lab\EvaluationLab.csproj --configuration Release -- --v2-pr4c-audit --batch <PR4b-batch> --export <PR4c-export>
```

The complete response is flushed to a pending file, then first-write
renamed to `first-adjudication-record.json`. The original submitted
answer file SHA-256, original judge file byte hashes, full adjudication
decision objects, unresolved decisions and content digest are preserved.
If the importer/audit fails, no valid adjudication record is published.

The audit independently reconstructs the **complete dispute census**
from A/B and their source-scoped batches, regenerates every public packet,
checks ZIP content and member census, and verifies the committed answer
record, source IDs, original conclusions and individual dispositions.

## Verification and remaining gates

```powershell
git fetch origin
git switch --detach origin/feature/v2-pr4c-source-grounded-adjudication
.\scripts\check.ps1

dotnet run --project .\tests\evaluation-lab\EvaluationLab.csproj --configuration Release -- --v2-pr4c-self-test
```

The model-free self-test reuses the real **PR2→PR4a→PR4b disposable
synthetic fixture**, creates official criterion and claim disputes,
a calibration mismatch, an agreed critical claim and a zero-dispute
comparator, then exercises clean imports, incomplete/duplicated/foreign
answers, forged proofs, false confirmed changes, tampered A/B records,
modified census bytes, private ZIP injection and edited accepted
adjudication records.

No real Ollama run is required for this infrastructure gate. An
owner-authorized small externally reviewed diagnostic rehearsal can
follow a full local GREEN and merge review.

**PR4c does NOT** independently establish source truth, authenticate
model report bytes to PR3b adapter calls, certify calibration, qualify
held-out datasets or calculate success rates. PR5–PR7 retain those
responsibilities and must explicitly block scoring until all
prerequisites are met.
