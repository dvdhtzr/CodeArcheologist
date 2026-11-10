# Gate C4 — Genuine Reviewer and deterministic reconciliation

**Status:** Proposed implementation. Owner-local GREEN required before merge.

## Architecture

C4 connects the previously verified C3 independent Investigator and Skeptic
to a third, **genuine Microsoft Agent Framework Reviewer** using local
Ollama `qwen3.5:9b`. The .NET host remains authoritative.

C3 completes both roles *before* Reviewer admission. The Reviewer sees the
two validated assessments together, not the original private prompts,
provider responses or undisclosed source contents. Its input distinguishes
Investigator-local and Skeptic-local handles: the same `E1` string in
different role sessions is **not** the same graph relationship.

The Reviewer receives hypotheses and concerns, identifies which concerns
relate to hypotheses, and recommends bounded dispositions. It is forbidden
to invent additional evidence, call the repository, or claim runtime proof.

## Host reconciliation and safety

The host binds the Reviewer output to the exact run and snapshot scope.
The schema allows:
- At most two decisions, one per Investigator hypothesis.
- An explicit recommendation of `plausible-hypothesis` or
  `contested-or-insufficient` with an explanation.
- Bounded, exact Skeptic concern IDs associated with each hypothesis.
- Explicitly unresolved *global* Skeptic concern IDs.

C1 validation requires every material Skeptic concern to be represented
**exactly once** (linked to a hypothesis or preserved globally unresolved).
Unknown, duplicated, missing or foreign IDs fail closed. No hypothesis may be
proclaimed structurally supported by the Reviewer, and no model-only
rejection is permitted without a separately host-proven structural violation.
The deterministic host issues final `GateCHostDisposition` records; the
`IsVerifiedFact` flag remains false for interpretive hypotheses.

An honest, entirely inconclusive run—zero hypotheses and/or zero concerns—
can be valid if the Reviewer reconciles the actual sets exactly and no
source truth is fabricated.

## Failure handling

Reviewer begins only after two independently validated C3 results, under
the C1 state-machine gate. Successful validated Reviewer output advances
`Reconciliation -> Completed`; completion denotes only a completed
three-role workflow, **not** semantic fact verification.

Invalid reviewer JSON/IDs/dispositions produce `ValidationRejected`;
provider/adapter failures produce `Failed`; deadline produce `TimedOut`;
caller cancellation produces `Cancelled`. None can reach `Completed`.
All diagnostics are bounded categories without raw provider output, imported
source text, exception messages or hidden chain-of-thought.

C4 Reviewer has one MAF call, a 90-second per-role timeout, bounded
structured response size, `ReasoningEffort.None`, 1536 output tokens,
and **no automatic retries**. C3 retains its independently verified
budgets and evidence constraints.

## Verification

### Model-free and regression

Run the standard `scripts/check.ps1`. C4 contract tests are wired into the
existing `--gate-c1-self-test` entry and check schema rigidity,
objection coverage, foreign/duplicate IDs, no verified-fact promotion,
no unsupported rejection, explicit unresolved concerns and all Reviewer
terminal failure transitions.

### Genuine MAF/Ollama (explicit only)

After a full local GREEN, run:

```powershell
dotnet run --project .\tests\maf-adapter-contract\MafAdapterContractProbe.csproj --configuration Release --no-build -- --gate-c4-run 3
```

The probe selects an existing verified saved HTTP entry-point candidate,
executes Investigator -> Skeptic -> Reviewer sequentially, and prints safe
role diagnostics and counts. It does **not** run imported repository code or
read the mutable registered source. All three attempts must yield
host-validated `Completed` workflows to mark C4 GREEN.

The completed workflow is *not* proof that its hypotheses are accurate;
C6/C7's independently judged comparative usefulness evaluation remains
required.

## Deferred

C5 evidence-linked report workspace, user-facing investigations,
persistence, SSE event timeline, DI/project evidence tool extensions,
autonomous tool loops, live mission-control visuals and 60-case quality
evaluation. No cloud inference or GitHub CI is introduced.
