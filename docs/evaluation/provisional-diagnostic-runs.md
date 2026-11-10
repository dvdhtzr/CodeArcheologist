# Provisional diagnostic lab runs

All runs are **provisional**: the labels are unsigned assistant drafts and the thresholds are provisional. They measure where the system stands; they are not acceptance results.

- **Cases:** the 12 diagnostic cases.
- **Evaluations per run:** each case gets the baseline, Gate B, and 3 Gate C trials, so 60 evaluations.
- **Model:** local `qwen3.5:9b`.

## Run 1 — before E1 (agent evidence contract `gate-c-evidence-capabilities-v1`)

Owner run on Windows, 2026-11-10.

| Comparator | Completed | Integrity breaches | Anchor recall | Objection coverage | Review flags |
|---|---|---|---|---|---|
| deterministic-baseline | 12/12 | 0 | 94% (by construction) | — | 0 |
| gate-b | 12/12 | 0 | 35% | — | 0 |
| gate-c | 36/36 | 0 | 35% | 100% | 5 |

**Findings:**
- **Review flags:** all 5 were false positives of the lexical screen. Each hit a cautionary sentence such as "does not guarantee", "NOT verified".
- **Gate C was unstable.** Anchor recall on identical inputs ranged from 0/2 to 2/2 across trials.
- **Gate C reports were mostly restated evidence caveats, not code-specific reasoning.** No role mentioned the lambda in C07 or the DI registration in F09.
- **Cause:** Gate B receives the package's bounded source excerpt, and the Gate C roles did not; they saw only evidence labels and positions. This also made the Gate B vs Gate C comparison unfair.

## E1 — source excerpt for Gate C roles (contract `gate-c-evidence-capabilities-v2`)

**Change:** each independent role's evidence view also carries the package's existing bounded excerpt (2,400 characters, with truncation shown). It is JSON-encoded inside the untrusted-data framing (rule 49).

Unchanged: Skeptic independence, host validation, checkpoints and citation rules. The bumped contract version makes pre-E1 checkpoints fail closed.

**Acceptance for keeping E1:**
- zero integrity breaches;
- higher Gate C anchor recall than run 1;
- lower trial-to-trial variance.

Otherwise E1 is reverted, and the measurement is recorded either way.

## Run 2 — after E1

Pending the owner's run.
