# C6c evaluation corpus

Versioned inputs for the hybrid evaluation laboratory (ADR 0020). The lab is `tests/evaluation-lab`.

- `lab-config.json` declares the budgets, trials, comparators and provisional thresholds. The budgets must **equal** the host's constants: `--validate-corpus` fails on any difference, and this file never reconfigures the host.
- `cases/<id>/case.json` identifies the case, pins the SHA-256 of every fixture file, and selects the entry point by written-syntax location (kind, file, line).
- `cases/<id>/labels.json` holds the expected answers:
  - required observations, each with a source anchor;
  - required uncertainties;
  - forbidden claims;
  - material issues.
- `cases/<id>/source/` holds **written-syntax fixture inputs only**. They are never compiled or executed. The lab copies them into a temporary directory before capture.

## Line endings

Fixture bytes are pinned exactly. `.gitattributes` marks `eval/**` as `-text`, so Git never converts line endings. If a checkout made before that rule existed shows CRLF hash errors, restore the files from the repository (PowerShell):

```powershell
Remove-Item -Recurse -Force .\eval\cases
git checkout -- eval
```

## Owner label sign-off

Labels start as `"status": "draft", "authoredBy": "assistant-draft"`. For each case:

1. Read `case.json`, `source/` and `labels.json`. Edit anything you disagree with. Anchors are `file:line` locations of the evidence a good report should cite.
2. Set `"status": "owner-approved"` and `"authoredBy": "assistant-draft-owner-reviewed"` (or `"owner"` if you rewrote the labels).
3. Run `--validate-corpus`, then commit the change yourself. Your commit is the signature.

Rules:
- A scored (non-`--provisional`) run requires every label to be owner-approved, and `lab-config.json` `thresholdStatus` must be `owner-approved-for-scoring`.
- Labels must be fixed before any agent output for that case is scored.
- Changing a label after seeing results requires a recorded reason and a new provisional run. Never relabel a failing result to make it GREEN.

## Curated repositories

`curated/repositories.json` pins 15 public, permissively licensed repositories: commit SHA, licence hash, subtree, and snapshot fingerprint. Acquire them outside the repository with `scripts/acquire-curated.ps1 -Target <dir>` (Windows) or `scripts/acquire-curated.sh <dir>`, then pass `--curated-root <dir>` to the lab. Nothing from these repositories is built or executed. Entry points and labels for these cases are assistant drafts awaiting your review, like the fixtures.

## Not in C6c-1

The following arrive in C6c-2 and C6c-3:
- change experiments (C6c-2b);
- held-out cases (owner-authored; SHA-256 seal only);
- blinded usefulness judging;
- the remaining 8 diagnostic repeat cases.
