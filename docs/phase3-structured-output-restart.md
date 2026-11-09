# Phase 3 restart: local structured-output contract first

Status: native Ollama Gate A passed 5/5 locally on qwen3.5:9b; focused MAF/OllamaSharp adapter probe committed, awaiting owner's compile and runtime verification. Created from verified deterministic `main`. No code from draft PR #25 is presumed valid.

## Why restart

Draft PR #25 established that MAF agents and Ollama can execute, but the end-to-end result is not reliable: schema violations, skipped optional tool calls, and citation failures. The original implementation attempted MAF workflow integration, tool invocation, schema output, and validation in a single slice. Do not merge PR #25 to solve this.

## Ground rules

- Local Ollama only, pinned .NET 10-compatible package versions and Microsoft Agent Framework; no cloud APIs, CI billing, Docker requirement, manual patch installation, or imported-source execution.
- Keep the verified snapshot, read-only evidence, authentication/CSRF, and deterministic citation rules on `main`.
- One new capability and one testable acceptance gate per PR; owner runs `scripts/check.ps1` and explicitly confirms GREEN before acceptance. Do not present code review as compilation.
- Before relying on an API, verify its signature against the exact pinned packages or compile a focused minimal spike. The previous code failed to compile after an unsupported provider-option experiment.
- Distinguish provider transport, schema compliance, source-reference validation, and claim quality. Never claim semantic truth from matching citation IDs.
- No arbitrary shell or repository source execution offered to agents.
- Fail closed on invalid JSON, invented references, missing unknowns, invalid scope, cancellation, over-limit output, provider errors, and incomplete orchestration.

## Milestone gates

### Gate A — minimal provider contract

1. Implement an isolated, model-backed local `ollama` contract probe inside trusted app/test code, independent of graph or MAF workflow plumbing. The first slice talks to the documented Ollama REST API directly; **it does not yet verify OllamaSharp or MAF adapters**.
2. Use actual installed OllamaSharp and MAF-compatible types, with supported schema/JSON constraints verified against exact package APIs.
3. One tiny schema (e.g. answer + unknowns), deterministic host validation, explicit unknown and failure categories, bounded time/output, and no retries hiding failures.
4. Run locally against the installed model and record aggregate compliance across repeated independent attempts. Model-free tests cover malformed, empty, oversize, extra text, cancellation and unavailable provider.
5. Owner confirms real local results before Gate B. If the provider cannot support the contract, stop and reassess model/adapter.

### Gate B — single Investigator

- MAF real agent consumes host-selected, SHA-256-verified saved snapshot evidence; no discretionary tool call required merely to receive it.
- Structured result must contain exact reference IDs and admitted unknowns; independent gate checks scope and type.
- Tests cover valid findings, fabricated IDs, wrong claim type, stale snapshot and source tamper.
- Demonstrate repeatable local end-to-end behavior before advancing.

### Gate C — separate Reviewer

- Add an independently invoked genuine MAF Reviewer with a host-controlled, evidence-bearing handoff.
- Reviewer receives verified evidence and the Investigator's *untrusted* claims, and must challenge them, not copy them.
- Each role's output and failure is individually observable. No silent retry, no credit for host evidence fetch as model tool call.

### Gate D — integration

- Only after A–C pass, expose a bounded browser investigation and actual MAF workflow trace.
- At least one owner-accepted local run with validated source references, explicit limitations, and no false runtime claims; repeatability evaluated across multiple runs.
- PR ready only after local `scripts/check.ps1` GREEN and browser acceptance.

## Deliberately out of scope

No new visualizations, additional static analyzers, agent roles, persistent task memory, semantic confidence scores, workflow checkpoints, or vault export until these gates pass.

## First action

Research/verify provider structured-output API signatures *before writing the integration*. Prefer the smallest compile-able provider probe over another unverified API change.

## Gate A provider probe — owner instructions

From the project root on the fresh branch, first run `./scripts/check.ps1` (the deterministic contract tests execute automatically). Then, with Ollama running and `qwen3.5:9b` installed, run:

```powershell
dotnet run --project .\tests\ollama-contract\OllamaContractProbe.csproj --configuration Release -- 5 qwen3.5:9b
```

The command posts five independent non-streamed schema-constrained requests to `http://127.0.0.1:11434/api/generate` using `format` as a JSON Schema and `think: false`. It does not use any user repository, files, credentials, or cloud endpoint. It logs category-only outcomes; no model prose is printed. A missing provider, timeout, unsupported schema, partial JSON or field-contract failure produces RED. No retries obscure failures. An all-GREEN provider result proves **only** this narrow native-Ollama contract, not the MAF adapter, source grounding, or agent behavior.

**Required next gate before an agent:** Compile a separate adapter probe against the exact pinned OllamaSharp and MAF versions, using verified API signatures and the same host schema validation. Do not infer adapter support from the REST probe alone.

## Gate A adapter probe — isolated compile and runtime verification

The native Ollama REST test returned 5/5 `valid` on the owner's machine. A separate project now references **Microsoft.Agents.AI 1.28.0** and **OllamaSharp 5.5.2**, exercising `AsAIAgent`, `AgentRunOptions.ResponseFormat`, and `ChatResponseFormat.ForJsonSchema` with the same required fields and length constraints. This uses actual MAF `RunAsync`, not a manual HTTP substitute; it does not yet introduce evidence or tools.

From the project root on `feature/phase3-structured-output-contract`:

```powershell
dotnet build .\tests\maf-adapter-contract\MafAdapterContractProbe.csproj --configuration Release
```

Only if that builds GREEN, execute with Ollama running:

```powershell
dotnet run --project .\tests\maf-adapter-contract\MafAdapterContractProbe.csproj --configuration Release --no-build -- 5 qwen3.5:9b
```

The adapter project is intentionally not part of `scripts/check.ps1` until the pinned package signatures have been confirmed on the owner's Windows installation. A compile failure is a gate failure, not permission to improvise a replacement API. A valid run proves schema adherence for this minimal two-field contract, not source grounding, agent reasoning, or reliability at scale. No raw model response or user source is printed.

## Gate B implementation candidate (awaiting owner Windows GREEN)

Gate A native Ollama and real MAF/OllamaSharp each passed 5/5 independent
local attempts on November 9, 2026. The earlier text above recording
adapter verification as pending is superseded by this result.

The approved first Gate B slice adds a single genuine MAF
`FunctionalInvestigator` inside the existing ASP.NET Core API, using
the exact pinned, compile-verified Gate A packages. It does not reuse the
experimental PR #25 orchestration, add roles, expand UI, introduce a
new application, or expose arbitrary model-selected tools.

Authenticated/CSRF-protected POST:
`/api/v1/workspaces/{workspaceId}/repositories/{repositoryId}/snapshots/{snapshotId}/investigations/gate-b`
with body `{"entryPointId":"<saved graph candidate ID>"}`.

The application first calls `ReadEntryPoint` on an exact saved snapshot
and verifies its source integrity. The agent gets a bounded, host-selected
evidence package, and must supply schema-constrained JSON including
snapshot/fingerprint IDs, 1-3 candidate relationships, their exact edge
and endpoint IDs, and unknowns. The host independently checks the schema,
scope, candidate relation kind/status/endpoints, evidence-package membership,
and `CheckCitations`. Passing results are explicitly
`accepted-reference-valid-not-semantic-proof`, not proof of narrative
accuracy or runtime reachability. Provider, adapter, scope, citation and
timeout errors remain explicit inconclusive categories. No retries.

Model-free negative cases run via `scripts/check.ps1` and the existing
MAF adapter harness with `--gate-b-self-test`. The existing snapshot HTTP
suite also asserts unauthorized, wrong origin, CSRF, wrong snapshot,
invented entry-point and DI-only entry-point rejection without Ollama.
Real model tests remain manual and must use a pre-existing captured snapshot.

After switching to this branch, the owner first runs:
```powershell
.\scripts\check.ps1
```
Do not call this GREEN based on GitHub commit or review alone. A real-model
Gate B result must be run five independent times on an actual saved
candidate snapshot via the protected API (or an agreed local harness).
Gate B remains pending until both deterministic and real-model gates pass
and the owner explicitly accepts. The result's free-text statement is
not independently semantically proven merely because the structural edge
and citations are validated.

## Gate B real-model saved-snapshot runner

After the Windows `scripts/check.ps1` suite passes GREEN and Ollama is running
with `qwen3.5:9b`, execute the already-existing adapter test project:

```powershell
dotnet run --project .\tests\maf-adapter-contract\MafAdapterContractProbe.csproj --configuration Release -- 5 qwen3.5:9b
dotnet run --project .\tests\maf-adapter-contract\MafAdapterContractProbe.csproj --configuration Release -- --gate-b-run 5
```

The second command is the real Gate B test, not another tiny two-field Gate A
contract. It uses the existing API's services directly, finds the first eligible
saved C# HTTP mapping/attribute candidate across up to 20 local snapshots,
verifies its source package before inference, invokes the actual
`GateBInvestigator` five independent times through MAF/Ollama, and reports
only outcome categories, counts, snapshot identity and fingerprint. No
free-text model response or copied source is printed. It uses the normal
Code Archeologist user app-data store and requires that at least one saved
snapshot containing a candidate already exist. If none exists, capture one
using the existing UI first. The runner does not capture from mutable
source, run repository code, or require manually supplying IDs.

This is an initial real-model Gate B reliability exercise, not full semantic
verification or evidence of C# runtime behavior. Stop on RED and diagnose.

## Gate B thinking-budget investigation (November 9, 2026)

The first full real-MAF Gate B experiment returned `empty-model-response`
on all five attempts, while the rest of `scripts/check.ps1` passed.
A bounded native Ollama `/api/chat` diagnostic using the actual saved
evidence package and exact Gate B schema showed:

| Native diagnostic | Default thinking | Explicit `think: false` |
| --- | ---: | ---: |
| Final content characters | 0 | 1674 |
| Thinking characters | 8698 | 0 |
| Generation tokens | 2025 | 670 |
| Done reason | `length` | `stop` |

This isolates a real output-budget failure: reasoning used the generation
budget before a final answer could be produced. It does not prove that
the returned JSON passes Gate B's structural/citation validator.

OllamaSharp **5.5.2** source `MicrosoftAi/AbstractionMapper.cs`
was checked: `ChatOptions.Reasoning.Effort = ReasoningEffort.None`
maps to native `request.Think = false`. Microsoft Agent Framework
supports `ChatClientAgentRunOptions(ChatOptions)`. The production
Investigator now requests this explicit setting through MAF, retains
`AgentRunOptions.ResponseFormat`, and preserves every existing evidence
validation step. No model, schema, trust-boundary, agent or architecture
change was made.

**Next gate:** owner runs `scripts/check.ps1` on the new branch head;
only after GREEN, run `--gate-b-run 5` to test five independent *real
MAF* investigations. Any remaining inconclusive diagnostic must be
investigated, not silently repaired or retried. Gate B remains RED until
these checks and owner acceptance.

## Gate B candidate-status rejection investigation

After explicitly disabling thinking through MAF, one owner real-model
batch returned 4/5 structurally accepted; the next returned 2/5. Three
failures were `relationship-status-not-candidate`. The original
`SnapshotEvidenceTools.ReadEntryPoint` correctly returns multiple
source statuses (candidate HTTP edges, observed-syntax containment,
and sometimes provisional callees). The Gate B acceptance contract,
however, intentionally permits only two candidate HTTP edge kinds.

The agent-facing view now includes a separately named
`eligibleRelations` array restricted by the host to scoped candidate
`annotates-method` and `candidate-http-mapping` edges. Source
references, excerpt, unknowns and limitations remain available as context.
The authoritative original evidence package is **not filtered** for
host validation: all scope/status/kind/endpoint checks and
`CheckCitations` remain unchanged. The same projection is used by the
native diagnostic, keeping it comparable to the MAF path. Model-free
tests assert that observed-syntax/provisional relations are not selectable
and that references are preserved.

This is an intervention to reduce ambiguity, not an approval to
reclassify observed-syntax or provisional relationships as candidate.
Gate B still requires full owner `scripts/check.ps1` GREEN, then five
independent real MAF runs. The empirical 4/5 and 2/5 outcomes must not
be relabeled as GREEN.
