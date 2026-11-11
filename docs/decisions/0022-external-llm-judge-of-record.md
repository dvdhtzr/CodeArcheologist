# ADR 0022 — External LLM (ChatGPT) as judge of record for C7

Status: **approved by the owner on 2026-11-11**, against the assistant's recommendation. It amends ADR 0020 decision 2 and AGENTS.md rule 57.

## Context

The C7 thresholds for material-issue detection, actionable recommendations and usefulness gain over Gate B need judgment. ADR 0020 required blinded owner judgment and banned an LLM judge for metrics of record. The owner has decided to give no further manual answers. Instead, an external LLM the owner operates (ChatGPT) produces the judgments.

## Decision

1. **Judge of record.** For the C7 judgment metrics, the judge of record is ChatGPT, operated by the owner outside the application. This holds **regardless of its agreement with the owner's earlier answers**.
2. **Application boundary.** The application and lab never call an external service (rules 10 and 47).
   - The lab writes plain-text question packets (`--export-judging`) for the owner to upload.
   - It strictly imports the replies (`--import-judging`).
   - Packets contain only report text, citations, unknowns and the owner-approved material-issue descriptions. They contain no source files.
3. **Blinding.** Comparator names are never shown, and comparator-revealing claim prefixes are removed. A/B order is random per question, and questions are shuffled across packets.
4. **Census, not sample.** Every material issue of every investigate case is asked, and every investigate case is asked as a Gate B/Gate C pair, using primary trial 1.
   - Fixed denominators apply. A side without a completed report counts as not mentioning and not actionable, regardless of the answer.
5. **Calibration disclosure.**
   - Questions the owner answered earlier are mixed in unmarked: the run 6 paired checklist and the run 5 blinded usefulness pairs.
   - The import reports ChatGPT's agreement with the owner. Every C7 result built on these judgments carries that figure.
   - Agreement is a disclosure, not a gate.
6. **Strict import.** Malformed, unknown, duplicate or missing answers refuse the whole import, and nothing is recorded.
7. **No shopping.** The first complete import is the record (`judgments-of-record.json`) and is never replaced. A re-judging pass would need a new recorded decision, and both results would be kept.
8. **Metric definitions, fixed before any scored report was judged:**
   - material-issue detection = Gate C problems mentioned ÷ all labelled problems of investigate cases;
   - actionable recommendations = Gate C pairs marked actionable ÷ investigate cases;
   - usefulness gain over Gate B = (Gate C actionable rate − Gate B actionable rate) in percentage points.
   - Pairwise preference is also reported, as a secondary measure.
9. **Labelling.** Every judgment metric is reported as "ChatGPT-judged (ADR 0022)", never as owner-judged. `--verdict` shows each threshold with its source: deterministic or ChatGPT-judged.

## Consequences

- C7 can complete without manual answers.
- The result shows what an external LLM judged, with its measured agreement with the owner. It is not owner judgment.
- Known risks: LLM judges tend to favour longer, more elaborate reports, which may favour Gate C. Blinding is partial because the reports differ in structure.
- Uploading the packets sends report text about public open-source repositories to OpenAI, by the owner's choice.
