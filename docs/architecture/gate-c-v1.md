# Gate C v1 — Approved Multi-Agent Investigation Architecture

**Decision status:** The project owner approved all ten architectural choices and
authorized milestone C1 on November 9, 2026. This document records the
approved design baseline; it is not evidence of completed implementation.

## Objective

A local-first, evidence-bounded three-agent investigation that provides
actionable, traceable assessments of arbitrary saved code without mistaking
source syntax for compiled or runtime behavior. The host is authoritative for
authorization, evidence provenance, claim eligibility, validation, resources
and completion. Microsoft Agent Framework (MAF) provides genuine local model
agent execution through Ollama; inference does not certify source facts.

## Approved decisions

1. **Independence:** Investigator and Skeptic separately examine the same
   source scope; Reviewer reconciles their validated assessments afterward.
   The Skeptic does not see Investigator conclusions or tool history initially.
2. **Interpretation:** Agents may generate original hypotheses, challenges
   and questions, always separate from verified observations.
3. **Evidence:** Each agent may discover bounded context via versioned,
   application-owned read-only evidence tools, independently scoped to one
   authorized immutable snapshot.
4. **Skeptic:** Evidence-backed concerns influence review but cannot
   arbitrarily veto source facts; deterministic gates reject demonstrable
   structural errors.
5. **Reviewer:** Recommends dispositions, addresses every material challenge,
   and cannot promote unverified model interpretations to verified facts.
6. **Orchestration:** .NET host owns workflow state, budgets and transitions.
   Genuine MAF roles execute sequentially at first to accommodate local
   inference resources, while preserving initial logical independence.
7. **Reports:** Evidence-linked results, separate agent assessments,
   disagreements, safe structured execution events and explicit incomplete
   states; full live mission-control UI deferred.
8. **Evaluation:** Compare deterministic baseline, Gate B and Gate C against
   independently established case expectations. Distinguish improvements
   from richer retrieval versus gains from multi-agent reasoning.
9. **Acceptance:** 60-case corpus, zero critical integrity breaches, separately
   measured analytical quality and held-out generalization testing.
10. **Delivery:** Seven focused, owner-verified GitHub milestones C1–C7.
    Significant architectural changes require discussion before implementation.

## Baseline and trust boundaries

The merged Gate B agent uses the .NET 10 API, OllamaSharp, MAF, the
`SnapshotEvidenceTools.Version` evidence package and short selectable handles
that map back to canonical IDs owned by the host. The existing
`CheckCitations` confirms reference integrity and allowed structural claim
kinds; it does **not** verify model-authored narrative semantics. Gate C must
not weaken this distinction or change existing graph statuses.

Source acquisition remains explicit and immutable-by-application.
Roslyn syntax, provisional callees, DI registration candidates, HTTP endpoint
candidates and static .csproj patterns are not runtime-verified behavior.
No imported code execution, project evaluation, generators, arbitrary model
file/network access, or autonomous repository mutation is authorized.

The C1 `GateCContracts` and `GateCContractValidator` are **pure,
model-independent foundations**. They do not yet instantiate new agents,
provide discovery tools, persist runs or expose new HTTP endpoints.

## Agent and evidence contracts

- Each run has a host-issued ID and exact workspace, repository, snapshot,
  fingerprint, entry-point ID and evidence-contract version.
- Evidence is represented to the model using short, run/role/package-local
  handles. The host retains the canonical mapping and verifies each entry
  against the original authorized source package.
- The Investigator returns scoped hypotheses, selected observations, unknowns
  and follow-up questions. Interpretive prose remains unverified.
- The Skeptic's initial assessment records independent concerns, possible
  alternatives, relevant evidence and unknowns. It cannot target specific
  Investigator hypothesis IDs before seeing them.
- After independent validation, the Reviewer maps concerns to hypotheses,
  addresses each material concern or preserves it as globally unresolved, and
  recommends a disposition for each Investigator hypothesis.
- Possible recommendation categories: structurally supported (host-grounded
  observations only), plausible hypothesis, contested/insufficient, rejected.
  A model proposal cannot use the first category to certify its own hypothesis.
- The host alone publishes verified structural observations. Even three-agent
  agreement does not change a source-edge status or validate semantic prose.
- Output is schema-versioned, strictly bounded and fail-closed. Malformed,
  stale, foreign, duplicative or over-budget references are rejected.

## Workflow

1. Authorize request and verify the immutable source snapshot.
2. Prepare host-controlled run scope, initial evidence and budgets.
3. Execute independent Investigator and Skeptic MAF roles, sequentially at
   first, with separately authorized read-only tool contexts.
4. Validate role outputs individually; neither receives the other's initial
   assessment or discovery history before both have completed.
5. Admit Reviewer only after required independent gates pass.
6. Validate Reviewer output and deterministic disposition.
7. Publish evidence-backed observations, unverified hypotheses, challenges,
   unknowns, safe activity and exact completion status separately.

States: created, evidence-verified, independent-assessment,
reconciliation, completed, incomplete, cancelled, rejected. Role execution
has its own status. Completion never implies semantic proof. Incomplete
runs may retain validated source observations but must not claim a complete
three-agent review. No automatic model retries or free-form agent delegation
in this release.

A later milestone will calibrate explicit model/tool/time/output budgets,
record them in versioned configuration **before** final scoring, and enforce
them in the host. No live model run is required for C1.

## Reporting and observability

Versioned run results identify snapshot and analyzer versions, role
assessments, evidence accesses, challenge associations, reviewer recommendations,
host disposition, unknowns, safe diagnostic codes and bounded usage metrics.
Evidence-linked UI provides role, disagreement, source and timeline views.
Do not store raw private model deliberation or leak imported source text and
secrets in diagnostics. C5 defines minimum persistence of completed reports;
durable resumable agent workflows are deferred.

## Evaluation and numerical acceptance

Initial 60 investigation cases: 20 purpose-built fixtures, 15 curated real
repository cases, 15 deliberate change experiments and 10 held-out cases.
The holdout cannot be used to tune prompts, tools or projections. Label
source facts and required uncertainties independently of the evaluated agents.
Evaluation must declare fixed denominators and keep incomplete/inconclusive
results visible. An honest inconclusive result can be correct.

**Hard release blockers (zero accepted breaches):** incorrect snapshot scope,
fabricated or foreign citations, unsupported runtime claims promoted to
verified facts, imported code execution, unauthorized tools, silently
discarded material objections, failed workflow presented as completed, known
schema regression.

**Provisional analytical thresholds:** >=90% eligible complete workflows,
>=85% labeled material issue detection, 100% valid material objection coverage,
>=80% actionable recommendations on eligible reports, and >=15 percentage
points gain over Gate B on an independently judged *paired* actionable
usefulness metric. Report costs and completion separately; isolate improved
retrieval from multi-agent benefit where feasible.

Run 60 primary cases and two additional trials on 12 predefined diagnostic
cases = **84 Gate C evaluations**. This small engineering corpus is not a
statistically representative universal accuracy claim. Threshold changes
require explicit approval before held-out scoring, never retroactive changes
to relabel a failing evaluation GREEN.

## Delivery milestones and independent gates

- **C1 — Contracts and validation:** Versioned role/scope/challenge/disposition
  types; host reference/identity checks and model-free negative tests.
  No new role execution or UI. Check with local `scripts/check.ps1`.
- **C2 — Evidence discovery:** Allowlisted, read-only analyzer adapters;
  versioned capability registry; independent scope/budget/provenance tests.
- **C3 — Independent roles:** Real MAF Investigator and Skeptic, strictly
  scoped typed outputs, separate tool histories; genuine local model tests.
- **C4 — Reconciliation:** Real MAF Reviewer, all material objections handled
  or visibly unresolved, no semantic-fact promotion; incomplete-run behavior.
- **C5 — Workspace:** Evidence-linked report, source navigation, disagreement
  inspection, safe bounded activity records and appropriate report persistence.
- **C6 — Laboratory:** Versioned 60-case evaluation, comparator, fixed scoring
  definitions, held-out and repeatability discipline.
- **C7 — Acceptance:** Full local GREEN, comparative evaluation, integrity
  check, documented limitations and explicit owner release acceptance.

Each PR remains draft until the owner runs its local checks and reports GREEN;
the owner approves merges. GitHub Actions CI remains disabled. No cloud model,
paid API, Docker mandate or downloaded patches are introduced.

## Explicitly deferred

Open-ended debate, autonomous delegation, automatic correction, persistent
workflow checkpoint/recovery, arbitrary repo paths, full semantic/runtime
proof, third-party executable plugins, cross-repository orchestration,
transitive speculative impact, and full live mission-control UI.

## C1 implementation note

The first code milestone uses immutable typed contracts and deterministic
checks without making the model an authority. In particular, initial Skeptic
concerns are independent of Investigator hypothesis IDs, and every material
concern must be accounted for during later Reviewer reconciliation. Any
rejected output fails closed; model-free tests must cover foreign and stale
identity, duplicate/unknown handles, wrong statuses, absent concerns and
forbidden hypothesis fact-promotion.
