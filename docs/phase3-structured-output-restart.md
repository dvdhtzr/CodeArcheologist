# Phase 3 restart: local structured-output contract first

Status: Gate A provider probe implemented, awaiting owner's local compile and Ollama results. Created from verified deterministic `main`. No code from draft PR #25 is presumed valid.

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
