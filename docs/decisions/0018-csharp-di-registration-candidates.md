# ADR 0018 — C# dependency injection registration syntax candidates

- **Date:** 2026-11-09
- **Status:** Implemented in Draft PR; owner Windows and browser acceptance pending
- **Prerequisites:** ADR 0012 observed invocation sites, ADR 0017 Minimal API mapping candidates
- **Roadmap:** Analyzer-to-agent-tool readiness issue #21; real MAF/Ollama workflow #19

## Purpose

To explain a .NET application's behavior, future local investigative agents
need source-backed evidence of how services **may** be registered with the
dependency injection container. The approved architecture requires us to
avoid treating conventional method syntax as proof of actual runtime
registration, successful construction, or effective service lifetime.

## Source-only contract

A new `CSharpDiRegistrationInspector` uses Roslyn syntax trees over
the same SHA-256-verified, saved snapshot C# source bytes as the
existing evidence graph, with strict UTF-8 decoding, cancellation and
bounded file/text/site counts. It looks only for ordinary member-access
invocations with simple method spellings `AddScoped`, `AddSingleton`,
and `AddTransient`.

For every occurrence the inspector captures:

- Exact workspace/repository/snapshot identities, fingerprint, saved
  relative path, file SHA-256, source invocation span and line/column.
- Written receiver (max 160 chars), inferred `scoped`, `singleton`
  or `transient` **from method spelling alone**.
- Generic two-type `AddScoped<IService, Implementation>()` as a
  **written service/implementation pair**, not bound Roslyn symbols.
- Simple `AddSingleton<T>()` as `generic-self-or-service`,
  **without inventing an implementation type**.
- Generic single-type registration with an argument as
  `generic-service-with-argument`, distinguishing lambda/anonymous
  factory syntax from other unresolved factory/instance expressions.
- Exactly two ordinary `typeof(TService), typeof(TImplementation)`
  arguments as `typeof-service-implementation` candidate syntax.
- Unsupported overloads and dynamic arguments as `unresolved`.
  Long written type spellings are not silently truncated into a
  purported type identity.

A custom `custom.AddSingleton<T>()` is a **candidate false positive**
because the analyzer does not bind the method to
Microsoft.Extensions.DependencyInjection. Other forms, including
`TryAdd`, `AddKeyed`, assembly scanning, decorators, source generation,
conditional application and reflection, are not covered.

## Evidence graph v8

- Version: `csharp-snapshot-evidence-graph-v8`.
- New node: `di-registration-candidate` with source-only written
  lifetime/service/implementation/shape evidence and explicit caveat.
- New edge: `candidate-di-registration`, status **`candidate`**,
  from the registration syntax node to the **already captured**
  `invocation-site` node, matched by exact saved file path,
  SHA-256 and span.
- A missing/mismatched independent invocation site **fails HTTP 409**
  instead of inventing a source occurrence.
- Graph counts: `diRegistrationAnalyzerVersion`,
  `diRegistrationCandidateCount` and `diUnresolvedShapeCount`.
- Observed containing declarations remain a separate
  `contains-invocation` / `observed-syntax` relationship. The graph
  **never** creates service-to-implementation declaration edges merely
  from type name similarity, generic text or a DI method name.

The existing React graph view gains **Find DI registration candidates**
with bounded first-40 display, saved source hashes/locations, explicit
unknown shapes, direct **Inspect** navigation to the actual graph
invocation evidence, and an honest no-candidates result.

## Security and coverage boundaries

The inspector verifies saved bytes and does not re-read live registered
repositories, build imported projects, evaluate MSBuild, restore packages,
load imported assemblies, invoke generators/analyzers, run tests or execute
source. It makes no network calls. Limits: 250 C# files, 8 MiB combined
source text and 3000 registration syntax sites. Exceeding bounds fails
closed HTTP 413; invalid UTF-8 fails 422. The existing authenticated
graph endpoint, provenance checks and workspace/snapshot scope apply.

**Not proven:** Real service registration, interface implementability,
constructor suitability, scope rules, registration order/replacements,
factory type/behavior, conditional execution, service resolution,
lifetime/disposal behavior, or the container used at runtime.

## Acceptance

- Disposable C# fixture exercises generic two-type registrations,
  single-type registration, lambda factory syntax, two-`typeof`
  registration and an unsupported non-generic expression.
- A same-named unrelated method is retained as **candidate**, not
  upgraded to actual DI; a different method name is excluded.
- Every registration node has exactly one candidate edge to the
  independently observed call node with matching hash/line/span.
- Distinct source-only registration shapes and unresolved count are
  reported without invented implementation type edges.
- No existing route/callee graph classification or source hash changes;
  snapshot tamper, invalid text, bounds, local auth and original-source
  immutability checks stay green.
- User Windows `scripts/check.ps1` GREEN, then browser acceptance of
  populated DI candidate rows and working Inspect navigation, before
  marking Draft PR ready for owner squash merge.

Next gate under #21: typed, deterministic agent-ready evidence-tool
contracts, scope authorization, independent citation validation and
coverage/negative tests before implementing real MAF+Ollama agents.
