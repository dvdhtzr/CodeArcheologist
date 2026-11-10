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

Owner run on Windows. The summary lines were not captured. Qualitatively, the Gate C reports were unchanged in kind: they still mostly restated evidence caveats.

- In C07, no role mentioned the lambda, even though the excerpt (the full method span) contained `System.Func<string> render = () => Format(id);`.
- In F09, the DI registration lies outside the entry method's excerpt, so not mentioning it is expected there.

**Verdict:** E1 is not sufficient on its own. Its plumbing (excerpt plus version bump) is kept, because E3 depends on it.

**Diagnosis:** the roles were never asked a question about the code. They were asked only to "propose UNVERIFIED hypotheses" or "identify limitations", while the evidence they received is dominated by caveat text.

## E3 — fixed host-authored investigative questions (contract `gate-c-evidence-capabilities-v3`)

**Change:** each independent role answers a fixed, host-authored question about the code. The exact text is `GateC3IndependentRunner.InvestigatorQuestion` / `SkepticQuestion`.
- The Investigator states what the method does and which calls it makes, citing handles, and keeps what is unprovable in uncertainty.
- The Skeptic independently names unsupported statements and alternative readings of the code, without referring to any Investigator output.

Unchanged: schemas, validation, independence, checkpoints, and the limit of two items per role.

**Acceptance:** the same as E1, measured against run 1. Also check qualitatively whether the C07 reports mention the lambda.

## Run 3 — after E3

Owner run on Windows, contract v3.

| Comparator | Completed | Integrity breaches | Anchor recall | Objection coverage | Review flags |
|---|---|---|---|---|---|
| deterministic-baseline | 12/12 | 0 | 94% | — | 0 |
| gate-b | 12/12 | 0 | 35% | — | 0 |
| gate-c | 35/36 | 0 | **31%** | 100% | 15 |

- **Qualitative:** the reports became code-specific. All three C07 trials describe the lambda/delegate (`render = () => Format(id)`), which no earlier run did.
- **Verdict: E3 fails its acceptance criteria.** Recall fell from 35% to 31%, one Gate C run did not complete, and review flags rose from 5 to 15.
- **Cause (an assistant error in the question):** it allowed an empty handle for points taken only from the excerpt. The roles then described the code without citing evidence, trading traceability for readability.
- The scoring was not changed.

## E3b — the questions require citations (contract `gate-c-evidence-capabilities-v4`)

**Change:** every hypothesis or concern must cite the allowed handle of the evidence item it is about. The excerpt is used only to understand that item, and empty handles are reserved for genuine evidence gaps.

**Acceptance:** the same criteria, against run 1. If E3b does not beat run 1, E1 and E3 are reverted.

## Run 4 — after E3b

Pending the owner's run.
