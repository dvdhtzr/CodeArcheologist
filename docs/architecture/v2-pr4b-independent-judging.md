# V2 PR4b — Blinded Batch Judging, Calibration and First-Write Imports

**Status:** draft; owner Windows GREEN pending. PR4b only. **NOT READY FOR SCORING.**

This design extends the approved PR4a artifact chain without changing
production models, prompts, tools, checkpoint contracts, SQLite schema,
historical C6c/C7/R3-PR4 data, or existing PR4a export bytes.

## What PR4b does and does not establish

1. Read two disjoint PR4a **diagnostic** exports: target diagnostic packets and
   separately source-reviewed calibration packets. Both must share the same
   frozen source-review receipt and exact rubric SHA-256.
2. Require a **pre-written calibration key**, with distinct owner-declared
   author/reviewer IDs, grade and grounding expectations for every criterion
   and every report claim. Calibration key source/proof references must fall
   within independently source-reviewed PR2 source scopes. A local name
   does **not** authenticate that these are genuinely different people.
3. Mix both sets into one hash-shuffled blinded batch. Original PR4a packet
   bytes remain pinned and unchanged, while new anonymous BPK-nnn IDs replace
   the earlier PKT-nnn header. The private crosswalk and calibration key never
   enter the shareable ZIP.
4. Import **two complete separate external judging submissions**, slots A and
   B, each with a distinct owner-declared judge ID and session ID. Both judges
   must assess *every criterion and every report claim*, including unexpected
   unsupported assertions. A first complete import is immutable by app policy,
   written and flushed to a temporary file before atomic same-dir rename.
5. Record exact agreement/disagreement with frozen calibration expectations.
   Calibration agreement is disclosed but **never treated as a certification**
   or performance score. Without PR5/PR6 owner-approved calibration thresholds,
   all results remain **diagnostic/not-qualified-for-scoring**.
6. Independently revalidate each packet, ZIP entry, archived whole-record
   checksum, complete judgment census and calibration counts during audit.

**PR4b does not:**
- Invoke ChatGPT/Ollama or any model. All external judging is owner-operated.
- Prove judge identities, session independence, or authentic external reasoning.
  The application cannot verify two submissions came from different humans,
  different models or independent sessions.
- Establish that PR4a's owner-supplied report is the genuine output of a
  particular PR3b host invocation. Those run/attempt IDs are declarations.
- Judge or adjudicate disagreements between judges. PR4c handles that.
- Calculate source-grounded task success, confidence intervals, pass rates or
  a scored-study verdict. PR5–PR7 handle scientific qualification.

All hashes detect accidental/unauthorized modifications to frozen files
relative to the retained private key. They are NOT digital signatures
protecting against an attacker who controls the local file system.

## Inputs

**Both PR4a exports must already exist** and have passed PR4a's source-first
manifest and exact-report checks. They must contain different cases and
different report SHA-256 hashes, but refer to the same independent PR2
review-import receipt. Hold-out and change-impact inputs remain excluded
from this PR4b diagnostic path.

Create a calibration key outside any shareable directory, in the following
shape (replace example IDs/hashes and write actual independently reviewed
answers *before* distributing judge packets):

```json
{
  "schemaVersion": "v2-pr4b-calibration-key-v1",
  "rubricSha256": "REPLACE_WITH_ACTUAL_SHA256",
  "authoredBy": "calibration-source-author",
  "reviewedBy": "independent-source-reviewer",
  "expected": [
    {
      "packetId": "PKT-001",
      "grades": [
        {
          "criterionId": "OBS-01",
          "grade": 0,
          "grounding": "contradicted",
          "reason": "The referenced source line refutes the report statement.",
          "reportClaimRefs": ["CLM-001"],
          "sourceProofRefs": ["OBS-01:P1"]
        }
      ],
      "claims": [
        {
          "claimId": "CLM-001",
          "grounding": "contradicted",
          "materiality": "material",
          "reason": "The source uses different return logic.",
          "sourceProofRefs": ["OBS-01:P1"]
        }
      ]
    }
  ]
}
```

Expected calibration judgments are **private labels**, and their correctness
still depends on external source-based review. The program validates structure,
proof identities, and independence declarations, not semantic truth.

## Prepare a frozen mixed batch

From trusted repo root:

```powershell
dotnet run --project .\tests\evaluation-lab\EvaluationLab.csproj --configuration Release -- --v2-pr4b-prepare --root <v2-root> --review <PR2-review-folder> --official <PR4a-target-export> --calibration <PR4a-calibration-export> --labels <calibration-labels.json> --out <new-empty-batch-folder>
```

Output:

```text
<batch>/
  DO-NOT-UPLOAD-pr4b-private-key.json    # private crosswalk AND expected calibration answers
  upload-these-to-judge/
    INSTRUCTIONS.txt
    packet-001.txt
    packet-002.txt
    ...
  upload-these-to-judge.zip             # ONLY upload this archive
```

**Never share the private key, calibration labels, PR4a private keys or earlier
judge answers.** The owner must also decide whether the source code itself is
appropriate to upload to ChatGPT. The ZIP includes reviewed source excerpts
and actual report claims; do not upload proprietary source inadvertently.

The two external judge passes read the same public ZIP independently.
Provide only the SHA-256 digest of
`DO-NOT-UPLOAD-pr4b-private-key.json` as `batchKeySha256`, not the file
itself. PowerShell:

```powershell
(Get-FileHash -Algorithm SHA256 -LiteralPath '<batch>\DO-NOT-UPLOAD-pr4b-private-key.json').Hash.ToLowerInvariant()
```

## Judge submission and first-write import

Each external judge returns a complete JSON document using
`v2-pr4b-blind-submission-v1`:

```json
{
  "schemaVersion": "v2-pr4b-blind-submission-v1",
  "batchKeySha256": "REPLACE_WITH_ACTUAL_SHA256",
  "judgeId": "judge-pass-A",
  "sessionId": "session-A-unique",
  "assessments": [
    {
      "packetId": "BPK-001",
      "grades": [
        {
          "criterionId": "OBS-01",
          "grade": 3,
          "grounding": "supported",
          "reason": "Direct source declaration supports this statement.",
          "reportClaimRefs": ["CLM-001"],
          "sourceProofRefs": ["OBS-01:P1"]
        }
      ],
      "claims": [
        {
          "claimId": "CLM-001",
          "grounding": "supported",
          "materiality": "material",
          "reason": "The actual code on the cited line supports this claim.",
          "sourceProofRefs": ["OBS-01:P1"]
        }
      ]
    }
  ]
}
```

The example is a **shape**, not an answer key. The actual answer must include
every anonymous packet and every criterion/claim, exactly once. A `3` on a
normal source observation requires supported grounding and proof. A grade
of `null` denotes not judgeable—not automatically wrong. Required
uncertainty and forbidden-assertion criteria retain their own rubric rules.

Import the two passes into the SAME frozen batch directory, separately:

```powershell
dotnet run --project .\tests\evaluation-lab\EvaluationLab.csproj --configuration Release -- --v2-pr4b-import --batch <batch> --slot A --answers <judge-A.json>
dotnet run --project .\tests\evaluation-lab\EvaluationLab.csproj --configuration Release -- --v2-pr4b-import --batch <batch> --slot B --answers <judge-B.json>
dotnet run --project .\tests\evaluation-lab\EvaluationLab.csproj --configuration Release -- --v2-pr4b-audit --batch <batch>
```

The importer fails closed on unknown, missing, duplicate, malformed or
out-of-scope judgments, foreign proof/claim references, altered packets,
reused declared judge/session identities, existing slot records, and changed
calibration keys. Invalid submissions do not create accepted records. A
valid import always preserves full original judge claims and reasons plus
the exact submitted file SHA-256 and separate calibration differences.

Two separate owner-operated ChatGPT sessions are not truly independent
statistical witnesses; neither identity is authenticated. This must be
disclosed with any eventual result. PR4c will compare every material grade
and claim-level grounding decision without dropping disagreements.

## Local verification

```powershell
git fetch origin
git switch --detach origin/feature/v2-pr4b-independent-judging-calibration
.\scripts\check.ps1

dotnet run --project .\tests\evaluation-lab\EvaluationLab.csproj --configuration Release -- --v2-pr4b-self-test
```

The model-free test uses actual PR2 source export/import and PR4a packet
export for two separate diagnostic cases. It verifies tamper rejection,
private/public ZIP separation, full judgment census, missing claim and
foreign proof rejection, atomic first-write behavior, different judge/session
IDs, calibration disagreement disclosure and whole-judgment integrity.

Owner-local full GREEN and a post-GREEN architecture review are required
before merging. The branch remains separate from PR4c and scoring.
