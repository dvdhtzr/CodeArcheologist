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

Owner run on Windows, contract v4.

| Comparator | Completed | Integrity breaches | Anchor recall | Objection coverage | Review flags |
|---|---|---|---|---|---|
| deterministic-baseline | 12/12 | 0 | 94% | — | 0 |
| gate-b | 12/12 | 0 | 35% | — | 0 |
| gate-c | **36/36** | **0** | **57%** | 100% | 17 |

**Trial-to-trial spread** is the per-case gap between the best and worst of the three Gate C trials, in anchor recall.
- Average before (run 1): **0.79**. Average after (run 4): **0.46**.
- Improved: C01, C07, C12, F01 and R06 (each to 0.00), and F10 (1.00 → 0.50).
- Unchanged: C03, C06, F05, F09 and R02.
- **Worse:** C11 (0.00 → 1.00).

**Verdict: E1 + E3b meet all three pre-set criteria.**
- zero integrity breaches;
- recall 57%, above run 1's 35% and above Gate B;
- lower average spread.

**Caveats:**
- This is a provisional, small sample: 12 cases, 3 trials each, draft labels.
- Review flags rose from 5 to 17. These come from more concrete but hedged statements, such as "likely acts as an HTTP GET endpoint… unverified". They are review aids, not breaches. Usefulness and overclaiming need the owner's blinded judgment (C6c-3).

## E2 — up to three evidence views per role (contract `gate-c-evidence-capabilities-v5`)

**Change:**
- Each independent role picks one to three distinct evidence views, most useful first, in one strictly parsed structured answer. Before, it picked exactly one.
- The host fetches each view through the same bounded role session: every request counts against the 6-call budget, and overlapping items keep the same handle. The views are merged into one view, de-duplicated by handle.

Unchanged: the questions, excerpt, schemas, validation, independence, checkpoints and two-item limits.

**Acceptance:** measured against run 4, the current `main`:
- zero integrity breaches;
- Gate C anchor recall above 57%;
- average trial spread below 0.46.

Otherwise E2 is reverted, and the measurement is recorded either way.

## Run 5 — after E2

Owner run on Windows, contract v5.

| Comparator | Completed | Integrity breaches | Anchor recall | Objection coverage | Review flags |
|---|---|---|---|---|---|
| deterministic-baseline | 12/12 | 0 | 94% | — | 0 |
| gate-b | 12/12 | 0 | 35% | — | 0 |
| gate-c | **36/36** | **0** | **94%** | 100% | **9** |

- **Trial spread:** the average fell from 0.46 to **0.00**. All 12 cases were consistent across their three trials. C03, C06, C11 and F05 each went from 1.00 to 0.00.
- **Verdict: E2 meets all pre-set criteria.** Zero breaches, recall above 57%, spread below 0.46. Review flags also fell from 17 to 9.

**Caveats:**
- **What recall does and does not measure.** Anchor recall measures whether reports cite the labelled evidence locations, not whether their statements are correct or useful. Gate C now cites as completely as the deterministic baseline, which restates all evidence by construction. Whether Gate C adds *insight* beyond that restatement is the open question, and only the owner's blinded usefulness judgment (C6c-3) can answer it.
- **Perfect consistency** (spread 0.00) may partly reflect roles converging on the same view selection. Consistency is not correctness.
- The sample is provisional and small, and the labels are drafts.


## Blinded owner judgment of run 5 (C6c-3a tool)

The owner judged run 5's 12 diagnostic cases with `--judge` (Gate B vs Gate C, one random trial each, random LEFT/RIGHT order, names hidden until the reveal).

| | Count |
|---|---|
| Pairs judged | 12 |
| Gate B preferred | 2 |
| Gate C preferred | 2 |
| Ties | 8 |
| Actionable: Gate B / Gate C | 100% / 100% |

- **Reading:** on these cases the owner found **no usefulness difference** between the single-agent and multi-agent systems. Gate C's higher anchor recall (94% vs 35%) did not become a judged advantage. ADR 0020's ≥15-point usefulness gain over Gate B is not met on this sample.
- **Why this may be a ceiling:** most of the 12 diagnostic cases are small fixtures or one-edit change experiments, so both reports were actionable. Real-repository cases (R01–R15) are where differences, if they exist, are more likely to appear.
- **Caveats:** the run was provisional (its labels were still drafts when it ran); blinding is partial; n = 12 with one judge. This is direction, not the metric of record.

In the same session the owner approved all 50 label sets with `--review-labels` (status `owner-approved`, provenance `assistant-draft-owner-reviewed`).

## Run 6: the 15 curated real repositories (provisional, labels owner-approved)

Owner run on Windows, contract v5, all 50 labels owner-approved, thresholds still provisional. `check.ps1` GREEN in the same session.

| Comparator | Completed | Integrity breaches | Anchor recall | Objection coverage | Review flags |
|---|---|---|---|---|---|
| deterministic-baseline | 11/11 | 0 | 64% | — | 0 |
| gate-b | 11/11 | 0 | 50% | — | 0 |
| gate-c | 14/15 | **0** | **67%** | 100% | 5 |

- **Expectation cases:** R10, R12 and R14 were confirmed as `no-supported-candidate` (nothing invented). R15 was confirmed as failing closed (413), with no snapshot record.
- **Completion:** Gate C was incomplete once (R02 trial 1). Its 93% is above the provisional 90% threshold.
- **Recall on real code:** even the baseline reaches only 64%. Several labelled anchors, such as registration-site context, are locations no comparator cites. Gate C matches or exceeds the baseline. It cited both anchors in R02, R03 and R04, where Gate B cited one.

### Owner material-issue checklist (`--checklist`, default cap 5)

| | Gate B | Gate C |
|---|---|---|
| Problems mentioned (of 5 judged) | **0/5** | **4/5** |

There were 13 eligible problems. 5 were judged. 4 were not asked because the word-match screen saw no difference, and 4 fell beyond the cap.

**Reading:** on real repositories, where the reports differed, the owner found that Gate C mentioned the labelled real problem in 4 of 5 cases and Gate B in none. This is the first owner-judged evidence of a Gate C advantage. It fits the earlier tie on small fixtures, which have little to find beyond restating evidence.

**Caveats, stated plainly:**
- **Selection bias.** The word-match screen picks problems where the reports' word overlap differs. Gate C reports are longer, so the screen is likely to pick problems Gate C repeats. These counts describe differences found, not detection rates. The material-issue detection threshold (85%) is not measurable from this sample.
- n = 5 with one judge, provisional thresholds, partial blinding.
- An unbiased estimate needs a random sample of problems, not one chosen by difference.

### Owner checklist, every problem (`--checklist --every-problem`)

Every labelled material issue in run 6 was judged: 13 of 13 problems, no selection. That includes the 5 judged earlier.

| | Gate B | Gate C |
|---|---|---|
| Problems mentioned | 1/13 (8%) | **7/13 (54%)** |

- **Reading:** across every labelled problem on the real repositories, Gate C mentioned the real problem about seven times as often as Gate B, a 46-point gap.
- **Against the draft target:** Gate C's 54% is well **below** the provisional 85% material-issue detection threshold. It found about half of the problems.
- **Caveats:** one judge; n = 13; one random trial shown for repeated cases; partial blinding; thresholds still provisional.

## E4 (pre-registered): show Gate C the registration site of Minimal API mappings

**Why:** four of Gate C's six misses in the every-problem checklist (R01 M1, R05 M1, R08 M1, R11 M1) concern how a Minimal API endpoint is *registered*. Examples are endpoint-class or Carter module discovery, route-group prefixes and sibling authorization. For a Minimal API mapping, the agents' excerpt was only the `MapX(...)` call, so that context was never visible to them.

**Change (contract `gate-c-evidence-capabilities-v6`, evidence tools `snapshot-agent-evidence-tools-v2`):**
- Minimal API entry points get a separate, bounded `registrationContext`: the containing type's declaration header plus the nearest containing method, constructor or local function, or the surrounding text for top-level statements.
- It is extracted by a syntax-only parse of the hash-verified bytes, with no compilation, binding or project evaluation, and capped at 2,400 characters. When the region is larger, the tool keeps the member's opening lines and a window around the mapping, and reports the truncation.
- It is labelled as untrusted source text and as registration-site context, not a bound handler or verified route (rules 43, 44, 49).
- Gate B does not receive it. Attribute entry points are unchanged. The investigative questions are unchanged, so no hint words are added.

**Acceptance, measured on the 15 curated repositories against run 6:**
- zero integrity breaches;
- Gate C completed at least 14/15;
- Gate C anchor recall of at least 67%;
- owner checklist (`--checklist --single gate-c`, every problem): Gate C mentions **at least 9 of 13** problems (run 6: 7/13).

Otherwise E4 is reverted, and the result is recorded either way.

Unlike run 6's checklist, the single-system checklist is **not blinded**, because the owner knows it shows Gate C. Gate B is unchanged, so it is not re-judged.
