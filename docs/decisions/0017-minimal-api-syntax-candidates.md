# ADR 0017 — Minimal API mapping syntax candidates

- **Date:** 2026-11-09
- **Status:** Proposed implementation; owner Windows and browser acceptance pending
- **Architecture:** Approved Code Archeologist v0.2, Section 8 and functionality-map scenario
- **Prerequisites:** ADR 0012 (observed invocation sites), 0016 (controller HTTP attribute candidates), analyzer/tool readiness issue #21

## Motivation

Code Archeologist's own `Program.cs` uses `app.MapGet(...)` and
`app.MapPost(...)` rather than controller HTTP attributes. The first
HTTP-attribute inspector therefore honestly reported zero candidates
when analyzing this repository. That is an evidence-coverage gap,
not a failed scan or proof that no endpoints exist.

Before MAF/Ollama investigations use the graph as a typed tool,
we need source-level evidence for conventional ASP.NET Minimal API
mapping spellings. The output must remain a **candidate inventory**
and must never fabricate registered, reachable or effective routes.

## Contract

A separate `CSharpMinimalApiInspector` reads only SHA-256-verified
copied snapshot C# bytes. It uses Roslyn syntax (no metadata references,
MSBuild, project evaluation, compilation or imported code execution)
to observe **ordinary member-access invocation expressions** whose
rightmost unqualified simple member is exactly:

- `MapGet` — conventional GET
- `MapPost` — conventional POST
- `MapPut` — conventional PUT
- `MapPatch` — conventional PATCH
- `MapDelete` — conventional DELETE

Each occurrence retains the exact saved invocation path, file
SHA-256, source span, one-based line/column, written member and receiver
text (bounded to 160 characters), inferred verb, first argument
route-literal status and a **non-binding handler syntax shape**.

A first-argument ordinary string literal no longer than 256 characters
is recorded as `written-literal`. Missing, nonliteral, interpolated,
property-resolved, constant, conditional or unsupported expressions
are `missing-argument` or `nonliteral-or-unsupported`.
The handler expression is described as a lambda, anonymous method,
method-group-like syntax or unknown; it is **never resolved** to a
runtime delegate, source method, endpoint filter or implementation.

The scanner may discover `custom.MapGet(...)` or another unrelated
same-named method. This **false-positive risk is explicit**: the
scanner does not bind ASP.NET extension methods or verify the type
of the receiver. Dynamic and conditional code may likewise be
omitted or only partially represented.

## Evidence graph v7

Graph v7 joins every Minimal API candidate to an **already observed
invocation-site node** using the exact saved relative file path,
SHA-256 and source span. If a candidate does not match the independent
invocation syntax report, fail HTTP 409 instead of dropping it or
fabricating a target.

- Node kind: `http-minimal-mapping` — written mapping candidate.
- Edge kind: `candidate-http-mapping`, status `candidate`
  — links to the saved invocation syntax, **not** to a guessed
  handler method or a registered endpoint.
- Count fields: inspector version, candidate total and unsupported/
  nonliteral route total.
- The existing `contains-invocation` / `observed-syntax` edge
  independently identifies a containing method/constructor where
  supported; top-level registration syntax stays file-scoped.
- Existing controller `http-endpoint-attribute` candidates remain
  distinct. This implementation does not unify route identities
  or compose effective URLs.

The React graph exposes **Find Minimal API mapping candidates**
independently from the existing controller annotation panel,
with saved line/hash evidence, link classification, detailed limitations,
direct graph navigation **with scroll and keyboard focus to the selected evidence panel**, and an explicit zero-result message. Direct Inspect actions in the controller, callee and impact panels use the same focus/scroll behavior. The
display is bounded to the first 40 rows, while full data stays in
the authenticated, snapshot-scoped graph API.

## Safety and test gate

- Maximum 250 C# files, 8 MiB combined saved C# bytes and 3000
  candidate mapping invocations. Exceed a cap -> HTTP 413,
  never a partial result.
- Strict UTF-8; invalid source -> HTTP 422.
- Existing authenticated snapshot scope, immutable manifest integrity,
  graph caps and read-only source protections remain unchanged.
- No new API endpoint, network call, source execution, package restore,
  external model, SQL migration, NPM/.NET package or GitHub CI.
- Test conventional literal GET/DELETE and dynamic POST, a
  misleading unrelated `custom.MapGet`, and excluded
  `CustomMapGet`/`MapGroup`, with exact invocation node/hash/span
  join and observed method containment.
- Browser acceptance on a snapshot of Code Archeologist should now
  show candidate rows for `Program.cs` `app.MapGet/MapPost` calls
  (provided that file was captured and in inspector bounds).
- Exact route registration, handler binding, `MapGroup` prefixes,
  `MapMethods`, `MapFallback`, authorization, middleware, DI,
  dynamic conditions and real reachability are still unsupported.

This is **one finite analyzer-readiness milestone** toward issue #21.
Further DI registration and typed tool contracts should be developed
and verified before resuming the first MAF/Ollama workflow (#19).
