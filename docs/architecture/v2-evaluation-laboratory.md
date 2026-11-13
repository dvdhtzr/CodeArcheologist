# Evaluation Laboratory V2 — Approved Design and PR1 Boundary

Status: V2-D1, D2, D3 and D4 architecture approved by the repository owner.
Implementation status: **PR1 structural contracts and isolation only**. This document
does not attest that a V2 corpus is semantically reviewed or ready for scoring.

## Why V2 exists

The historical R3 PR4 study remains frozen and inconclusive. Post-hoc review found
that some material issue labels required historical before/after information while
the compared model workflows investigated after-only source. V2 must independently
establish source truth, evidence availability, and answer quality before producing
a credible score. Historical C6c, C7, and PR4 artifacts, verdicts and labels remain
unchanged, and adaptive-agent expansion is frozen.

## Approved V2 task families

- **Current code:** answer from one pinned source snapshot.
- **Change impact:** answer from governed and authorized before/after evidence.
  Scored change-impact analysis is blocked until a production-supported evidence
  interface exists; an evaluator-only shortcut is not acceptable.
- **Evidence boundary:** correctly recognize unsupported conventions, unavailable
  evidence, and limits on conclusions.

Operational integrity and reliability are scored separately from code semantics.

## The three separate questions

1. **Source truth:** what does the pinned source actually establish?
2. **Answerability:** what evidence was initially available or retrievable under
   the comparator's authorized scope and budgets, and what was actually delivered?
3. **Answer quality:** did the report provide a correct, sufficiently grounded
   answer, with justified uncertainty and no critical unsupported assertions?

Truth, availability and judgment are independently versioned. An accepted source
hash is not proof of C# semantics; a saved receipt is not proof that the model saw
its payload; an approved draft label is not semantic proof.

## V2 schema identities

The isolated namespace `CodeArcheologist.EvaluationLab.V2` defines:

| Identity | Intended later responsibility |
|---|---|
| `v2-case-v1` | Developer question, partition, task type, source and scope |
| `v2-ground-truth-v1` | Atomic, source-anchored expectations and reviews |
| `v2-answerability-v1` | Criterion and comparator evidence-access classification |
| `v2-evidence-ledger-v1` | Actual host request, return and model delivery history |
| `v2-judgment-v1` | Criterion-level grade and source-grounding review |
| `v2-adjudication-v1` | Independent dispute review and append-only disposition |
| `v2-judgment-record-v1` | Finalized judging record bound to a rubric |
| `v2-experiment-manifest-v1` | Frozen case/protocol/execution identity |

The PR1 validator checks **structure only** for case and ground-truth documents,
including versions, IDs, declared source hashes, safe relative paths, task-specific
before/after metadata, source-proof reference consistency, and strict JSON decoding.
Proof of source semantics, actual snapshot bytes, answerability, model visibility,
independent grading and statistical quality are intentionally **not implemented**.
These are future PR2–PR7 gates.

## PR1 command surface

From the repository root:

```powershell
dotnet run --project .\tests\evaluation-lab\EvaluationLab.csproj --configuration Release -- --v2-self-test
dotnet run --project .\tests\evaluation-lab\EvaluationLab.csproj --configuration Release -- --v2-validate --root .\eval\v2
dotnet run --project .\tests\evaluation-lab\EvaluationLab.csproj --configuration Release -- --r3-pr4-validate
.\scripts\check.ps1
```

`--v2-validate` reports the number of structurally valid cases and explicitly
says **NOT READY FOR SCORING**, even if every structural check passes. An empty
V2 corpus is permitted at PR1; no scored study is performed.

`--v2-self-test` uses synthetic, disposable inputs and needs no Ollama.
Neither command reads a user's real workspaces or executes imported repositories.

## Approved implementation roadmap

1. **PR1:** isolated structural contracts, parser/validator and negative tests.
2. **PR2:** independent source-first ground-truth authoring and validation.
3. **PR3:** per-comparator answerability preflight and actual evidence delivery
   attribution, reusing authoritative production evidence and checkpoint contracts.
4. **PR4:** calibrated source-grounded batch judging, blinding and adjudication.
5. **PR5:** supported-task-success scoring, sealed census and cluster-aware reporting.
6. **PR6:** diagnostic qualification with model-free and real Ollama checks.
7. **PR7:** a protected held-out corpus (~60 cases, ~20 independent repository
   families), frozen manifests and scored-study preflight.

First scored study: estimate the existing fixed Gate C workflow's absolute ability
to produce supported developer answers. Adaptive comparison is a **separate future
study** requiring its own held-out corpus, statistical power justification and
explicit approval. Historical 50 cases are for diagnostic development only.

## Safety and governance

The historical C6c/C7/PR4 validator and reports are not changed by PR1.
All project safety constraints in `AGENTS.md` continue to apply: verify exact
source identity, do not execute imported source or project builds, preserve
typed and immutable evidence, work in disposable isolated data directories.

The complete Windows `scripts/check.ps1` verification and owner-local GREEN
are mandatory before merging. Real Ollama regression is separate. This project
does not use GitHub CI as a substitute for local verification.
