# Original Product Path Checkpoint — PR5a

**Review basis:** `docs/architecture/Code-Archeologist-v0.1.md` and
`docs/architecture/Code-Archeologist-v0.2.md` (both remain unmodified).
**Date:** 2026-11-14. **Status:** scope guardrail for the PR5a diagnostic
provenance bridge. This file does **not** amend the original approvals.

## Product destination, unchanged

Code Archeologist is a **local-first, multi-repository software architecture
explorer for developers**, not an AI research benchmark dashboard. The
developer-facing questions are still:

1. What does this unfamiliar application do, and how is it organized?
2. Where do its business rules and workflows live?
3. Which frontend, API, service and data components depend on one another?
4. What could break after a proposed change?
5. What source proves each conclusion, and what remains uncertain or stale?

The desired experience remains visual Mission Control, evidence-linked
architecture and impact exploration, source navigation, honest limitations,
and eventually human-maintained institutional memory/Obsidian, portable
distribution and safe extensibility. Deterministic analyzers establish bounded
source observations; local MAF/Ollama agents investigate and explain; the
host, not the models, validates evidence and controls state.

## Progress and the risk of drift

Implemented foundations include safe snapshot capture, C# structural
analysis/graph, Mission Control/Atlas navigation, the bounded three-role
investigation, durable checkpoints and source-linked reports. User-local
verification shows operational GREEN, and two PR3b live Ollama smoke runs
succeeded. **None of those are evidence that unfamiliar real-code
developer answers are semantically reliable.**

The initial design also promised polyglot workflows, full-text/vector
retrieval, knowledge/vault review, plugins and easy cross-platform
distribution. Do not claim that the existing V2 judging implementation
delivers these product functions. In particular, the evaluation laboratory
has become a large fraction of recent engineering effort. Its work is
legitimate only when it helps us determine whether actual *developer-facing
answers* are trustworthy or informs the next specific product improvement.

## PR5a bounded permission

PR5a is **one read-only report provenance bridge**, not a new investigation
pipeline, public quality score or AI behavior-change experiment.

- Observe the original persisted `GateC5Report` JSON bytes using a
  *read-only* SQLite connection with a consistent transaction.
- Trace original source snapshot identity, attempt lineage, accepted
  checkpoint digests, and actual PR3b call event digests, when present.
- Keep host-injected cautions distinct from model-authored assessments.
- Mark missing receipts, prior/continued checkpoints, absent observations,
  nonterminal runs, and unsupported linkage as explicit blockers.
- Never create a provider-output checksum retroactively: PR3b hashes
  model-facing prompts, **not raw provider responses**.
- Never describe SHA-256 as semantic proof or a signature; no raw user
  database or source files are uploaded.
- No production prompt, model, evidence budget, SQLite schema, worker,
  checkpoint, source snapshot or historical label changes.
- Output is private and diagnostic, always **NOT READY FOR SCORING**.

After the PR5a host bridge is GREEN, perform **one owner-observed real
developer workflow** against a pinned unfamiliar repository: navigate the
architecture, ask a meaningful scoped question, inspect the source
evidence in Mission Control, and judge whether the answer is useful,
correctly cautious and actionable. Record material defects and the user
experience separately from laboratory protocol problems.

Before advancing to PR5b/PR5c, review whether findings justify more
scoring code or instead require work on C#/polyglot analysis, source
navigation, knowledge continuity, or packaging. The original product
promise determines priority, not completion of evaluation milestones.
PR5b/5c remain approved in principle, not scientific proof of readiness.

## What the PR5a gate must demonstrate

- Exact persisted report bytes, not owner-transcribed JSON.
- Read-only database open (no migrations, writes or ownership claim).
- Snapshot/run/attempt/continuation identity and checkpoint digests either
  revalidated or distinctly marked missing; never fictitious re-execution.
- Real model **adapter calls** properly differentiated from evidence that
  provider bytes were accepted, parsed or understood.
- No model-created report claims from host-generated caution fields.
- Fixture tests for missing reports, wrong source identity, truncated or
  foreign lifecycle, tampering and immutability; full local GREEN.
- Draft PR remains unmerged until owner's local verification and approval.

This checkpoint keeps PR5a instrumental to the original app rather
than allowing evaluation machinery to become the destination.
