# Gate C3 — Independent Investigator and Skeptic (first real MAF slice)

**Status: implementation proposed, awaiting owner-local verification.**

This milestone adds **two genuine independent Microsoft Agent Framework
agents** to the existing local .NET 10 API codebase, using local
`qwen3.5:9b` via OllamaSharp. C3 is intentionally a non-HTTP bounded local
execution slice: it does not expose an end-user API/UI, persist investigation
runs or add the Reviewer. Those follow in C4/C5.

## Architectural continuity

- C1 owns versioned independent-role schemas and deterministic claim gates.
- C2 owns exact-snapshot, per-role read-only capability sessions and handles.
- C3 uses those contracts without modifying the saved evidence graph.
- C4 will reconcile separately validated role assessments through a Reviewer.
- No source content or model output is an instruction to access external tools.
- No imported source code, project builds, project evaluation or scripts run.

## Execution

`GateC3IndependentRunner.RunAsync(snapshot, scope, runId, cancellationToken)`
verifies and opens separate Investigator and Skeptic role sessions before
calling the model. Each role gets a fresh MAF `AIAgent` and a separate
Ollama-backed client with independent instructions.

Each role performs at most **two model calls**:

1. **Capability selection**: the agent selects exactly one of the four
   registered C2 capability names in a strict JSON schema. The host validates
   the enum and invokes that read-only capability on the role's session.
2. **Role assessment**: the agent receives its own bounded evidence view and
   returns a schema-constrained hypothesis or concern assessment, citing only
   visible short handles. It must state an explicit uncertainty.

The host binds `runId`, exact snapshot scope and role identity itself.
The model is not invited to copy or supply canonical graph IDs. The host
validates model structure, evidence selection and the role's exact
`GateCEvidenceSession` before accepting an assessment. The Skeptic does
**not** see the Investigator's prompts, conclusions, evidence handles or
tool ledger. The initial Skeptic concerns cannot name Investigator hypothesis
IDs; those associations belong to C4.

The roles execute sequentially to avoid GPU contention on the local model,
but their **contexts are logically independent**. If Investigator validation
fails, the run stops incomplete rather than spending additional inference on
the Skeptic. If Skeptic validation fails, the run stops incomplete. C3 success
ends at `independent-assessment`, **not `completed`**, because no Reviewer
has run.

## Model and host budgets

- Genuine MAF/OllamaSharp `AIAgent.RunAsync` for each role.
- `ReasoningEffort.None` to prevent hidden-thought output budget exhaustion
  observed in Gate B.
- JSON-schema-constrained per-role outputs and one capability selection.
- Maximum 1 selected C2 tool request per role; C2's 6-call tool budget
  remains a stricter upper bound for future expanded retrieval.
- Maximum 2 proposed hypotheses or concerns per role.
- Maximum 1536 output tokens and 8192 returned characters per model call.
- Maximum 90 seconds per role, linked to caller cancellation.
- No automatic model retries.
- Provider errors return bounded safe diagnostic categories without raw
  exception messages, source excerpts, or model output.

A role may validly report no hypotheses or concerns while stating an
unknown. This is a schema-valid, **not semantically proven** result.
Analytical usefulness will be tested through the approved Gate C evaluation
laboratory, not inferred from a 5/5 format success.

## Verification

The model-free parser/negative contracts run as part of
`scripts/check.ps1`, through the existing `--gate-c1-self-test` mode.
They cover capability allowlisting, strict role-specific response shapes,
unexpected/duplicate properties, invalid handle selections, empty/invalid
run IDs, and host rejection of foreign handles.

After local `scripts/check.ps1` GREEN, the owner separately runs a
**genuine MAF/Ollama** probe against an already saved and verified C# HTTP
candidate, from the existing local user app data:

```powershell
dotnet run --project .\tests\maf-adapter-contract\MafAdapterContractProbe.csproj --configuration Release --no-build -- --gate-c3-run 3
```

The probe searches the first 100 saved snapshots and never reads the
registered mutable source or compiles imported project code. Optional
third argument: `http-minimal-mapping` or `http-endpoint-attribute`.
It reports only run status, roles, diagnostic categories, counts and
bounded tool usage, not raw imported source or model reasoning.

**C3 acceptance requires** the full local suite GREEN and at least three
independent, genuine local MAF two-role attempts accepted by the existing
host gates. If RED, diagnose and correct the milestone PR; do not pretend
incomplete assessments are success.

## Explicitly deferred

Reviewer, challenge-to-hypothesis mapping, host final disposition,
source-backed semantic truth tests, further evidence capabilities such
as DI/project context, HTTP endpoints, UI, run persistence, autonomous
delegation, rich tool loops and the full 60-case quality evaluation.
