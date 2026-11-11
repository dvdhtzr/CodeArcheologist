# PR 3: Host-written syntax cautions in Mission Control

## Scope and non-goals

This is a **presentation-only** change. It consumes the PR 2 report contract
(gate-c5-investigation-report-v2) and makes no changes to the model workflow,
prompts, static analyzer, evidence mapping, source API, reports, checkpoint
persistence, evaluation corpus, or scoring.

In Findings, a section called "Code-analysis cautions (not AI)" appears
**separately from** the Investigator hypotheses, Skeptic objections, and
Reviewer verdicts. It precedes those agent sections but does not alter them.
Its text comes only from the host-authored persisted caution collection.
React renders warning and path text as escaped plain text, never as HTML
or Markdown. A warning is not a verified runtime claim, a compiler result,
an authorization determination, or proof of reachability.

## Host inspection states

- **Verified with cautions:** Show the five allowlisted caution kinds by
  human-readable title, the source-syntax statement, explicit host attribution,
  analyzer version and the exact saved-source anchors.
- **Verified with no cautions:** State that the limited current rules found no
  cautions for the selected entry point. Do not assert the code is correct,
  reachable, secure, or free of other issues.
- **Not evaluated:** State that the inspection was not completed and the
  result is unknown rather than a verified absence.
- **Historical v1 report:** State the report predates host cautions. The
  caution section remains visible with no invented inspection result.
- **Malformed or unsupported data:** Fail closed; do not render warnings or
  convert missing/incompatible data into an empty verified list.

## Source links and trust

An anchor with a verified saved-source path, SHA-256 and positive line opens
the **existing bounded snapshot-source panel** (GET /source through
ApiClient.source). There is no new endpoint, route concatenation, file-system
read, or execution of imported code.

Project-file candidates with null source spans are **shown as file-level
context**, never made into a made-up line-one hyperlink. The server still
authoritatively checks the path, SHA-256 and scope; the client validates
warning provenance only to avoid presenting visibly inconsistent data.

## Model-free acceptance

- Strict version, count, kind, status and analyzer provenance checks.
- Verified-empty versus not-evaluated versus historical v1 cases.
- Malformed, duplicated, incompatible and forged-looking data is withheld.
- Exact source path/hash/line propagation; file-level links are not invented.
- Caution statements and source snippets remain plain escaped text.
- Existing agent verdict and role-local citation paths are unchanged.
- Full local scripts/check.ps1 GREEN is required before review; UI visual
  confirmation is recommended on a real saved v2 report.

No claimed improvement in agent reasoning or the C6c material-issue
detection score is part of this PR.
