# ADR 0020 — First genuine local MAF + Ollama investigation (vertical slice)

- **Date:** 2026-11-09
- **Status:** Draft implementation; .NET build and browser execution not yet verified
- **Design baseline:** Approved Code Archeologist v0.2; ADR 0019; issues #19 and #21
- **Dependency:** Analyzer readiness #21 closed only after owner accepted merged PRs #18, #22, #23, #24

## Why this is a real agent workflow

An explicit authenticated browser action chooses a source-level HTTP
entry-point candidate in one immutable workspace/repository/snapshot.
`SnapshotEvidenceTools.ReadEntryPoint` validates the exact graph identity,
file hashes and source spans *before model invocation*. The app instantiates
two actual `Microsoft.Agents.AI.AIAgent` objects using a local
`OllamaSharp.OllamaApiClient` provider and composes them in a directed
`WorkflowBuilder(FunctionalInvestigator).AddEdge(... SkepticReviewer)`
workflow executed by `InProcessExecution.RunStreamingAsync`.
The workflow consumes MAF executor/agent-response events and actually
registers a role-owned `AIFunctionFactory.Create` read-only tool for
each agent. This is **not** two fake, deterministic persona responses.

## Deliberate restrictions

- Localhost-only Ollama at `http://127.0.0.1:11434`; default model
  `qwen3.5:9b` configured in checked-in
  `src/CodeArcheologist.Api/appsettings.json`. User can configure
  model via local app settings; no Azure, cloud subscription, secrets
  or automatic model installation.
- Explicit session-authorized, CSRF-protected `POST .../investigations/local-maf`
  with only `{ "entryPointId": "<exact candidate ID>" }`.
- Both roles have only a **parameterless**, application-owned,
  read-only evidence function bound to the already verified snapshot
  package. They cannot supply arbitrary paths, commands or external
  URLs. Role-specific tool use is recorded by the host, not by
  model-supplied arguments.
- Each role may make 1–4 evidence-tool calls for an accepted run;
  one investigation at a time. Four-minute deadline and request
  cancellation. This slice has at most one invocation of each agent
  in one directed MAF workflow; it does not retry bad model answers.
- Each role must produce a JSON object with bounded summary and
  1–8 findings. Findings have a constrained source-observation or
  candidate-relationship claim kind, 1–8 cited IDs and at least
  one explicit unknown. Overlong/invalid/non-JSON output fails
  closed. `SnapshotEvidenceTools.CheckCitations` independently checks
  typed citations against the exact immutable evidence package.
- No claim is accepted based merely on a model finishing:
  two distinct MAF executor responses, role-owned evidence-tool
  invocations and two successful independent citation checks
  are all required. A provider HTTP error, MAF executor failure,
  cancellation, timeout, invalid output, hallucinated citation or
  incomplete role/tool sequence produces **inconclusive**.
- Transport, workflow result and citation-reference quality remain
  separately visible in UI along with actual role/tool event trace,
  structured findings, unknowns and diagnostics. No fabricated
  numerical quality/confidence scores.
- Even an accepted role result means **citation IDs exist with the
  declared evidence type**; model narrative truth, actual route
  registration, runtime behavior, compiled calls, real DI binding,
  and model prompt-injection resistance remain **unverified**.

## Security & acceptance

No imported code/project execution, MSBuild evaluation, restore,
source generators, imported assembly loading, shell tool, live
repository mutation, vault write, or external AI service. Only
verified snapshot data is sent to localhost Ollama. A saved method
comment or string is untrusted input to the model, not a privileged
instruction.

Pinned new NuGet packages: `Microsoft.Agents.AI` 1.28.0,
`Microsoft.Agents.AI.Workflows` 1.28.0 and `OllamaSharp` 5.5.2;
verify restore and runtime API compatibility locally. The official
Microsoft Agent Framework samples are our primary source for the
`AsAIAgent`, `WorkflowBuilder`, `StreamingRun` and turn-token
integration; these are evolving interfaces, so owner build is the
authoritative gate.

The disposable tests cover unauthenticated/forged-CSRF launch,
foreign snapshot scopes, non-entry-point ID, malformed ID and
tool-authorization failure **without invoking Ollama**. A real
local browser smoke run must additionally verify both MAF executors,
real evidence-tool calls, trace and structured citation outcome.
A result of `inconclusive` is an observed failure, not success.
If the Ollama model cannot reliably call tools, adjust the agent
tool-use protocol or pick another locally available capable model
in a subsequent focused PR, *not* by falsely treating a no-tool
model response as an investigation.

### Explicit deferred capabilities

Durable checkpoint/restart, long-running job persistence,
multi-entry-point investigation, calibrated semantic correctness,
independent model critique beyond typed citation checks, held-out
quality harness, retry/correction budget, model selection/setup
wizard, vault writes and wider agent graph. The first slice is a
**real but deliberately experimental** sequential workflow.
