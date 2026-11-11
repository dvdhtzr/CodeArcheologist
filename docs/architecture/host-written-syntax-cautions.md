# Host-written syntax cautions — PR 1 contract

Status: owner-approved design; Windows verification pending.

## Purpose and boundaries

The analyzer produces up to five deterministic, host-authored cautions for
one selected HTTP entry-point candidate. The source is a SHA-256-verified saved
snapshot, never a live repository. Cautions report written syntax and
the limits of available evidence. They do not assert real ASP.NET symbol
binding, route registration, effective URLs, authorization policy, successful
compilation, or runtime reachability.

A caution is not a model hypothesis, reviewer decision, verified fact, or
evaluation label. No label descriptions or case identifiers participate in
production trigger logic.

## Versioned contract

- Analyzer: roslyn-host-cautions-v1.
- Evidence package: snapshot-agent-evidence-tools-v2, with separate
  HostCautions and HostCautionAnalyzerVersion fields.
- Capability version: gate-c-evidence-capabilities-v6.
- Checkpoint contract: gate-c6b-role-checkpoint-v2. Older checkpoints fail closed.
- Each caution has Kind, Id, Statement, AnalyzerVersion,
  Status=host-syntax-caution, and exact snapshot-file SHA-256 anchors.
- File-level project anchors intentionally have no line or span. They support
  project-file presence, not effective project membership.
- An empty caution collection never implies verified behavior.

## Rules

| Kind | Trigger | Nonclaim |
| --- | --- | --- |
| unobserved-registration | A written mapping inside a method has no provisional-local caller edge in the bounded source graph | No assertion that the method is never called |
| route-group-receiver | A mapping receiver is a written MapGroup call, or a non-reassigned same-scope local initialized from one | No effective route composition or verified ASP.NET binding |
| sibling-modifier-difference | Same written receiver and lexical registration scope, different RequireAuthorization / AllowAnonymous modifier sets | No effective runtime security inference |
| conditional-invocation | Direct invocation inside a conditional branch, catch branch, or after a direct guard exit | No path feasibility or execution claim |
| multiple-project-candidates | Two or more safely parsed static .csproj file candidates | No effective compilation membership or cross-project resolution |

The method/conditional rule respects nearest-execution-scope classification:
nested lambdas, local functions, accessors and other unsupported scopes do
not become direct invocations of enclosing methods. The conditional trigger
is not complete control-flow analysis. Ambiguous aliases and reassigned
route-group receivers suppress a caution rather than guess.

## Model isolation

The Investigator, Skeptic, Reviewer and single-agent comparator still see
only references, relations and unknowns, not HostCautions. Report/UI and
evaluation export are explicitly postponed to PR 2 and PR 3.

## Model-free checks and future evaluation

The lab's existing self-test will verify positive, negative and adversarial
source shapes, anchors against the saved snapshot manifest, model-projection
isolation, and wording independence from owner-approved material-issue labels.

No model outcomes or external evaluation metrics are claimed in PR 1.
The pre-registered curated-repository comparison and later held-out tests
remain pending.
