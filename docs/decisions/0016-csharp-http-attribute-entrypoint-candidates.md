# ADR 0016 — C# HTTP attribute entry-point candidates

- **Date:** 2026-11-09
- **Status:** Draft implementation; owner local/build and browser gates pending
- **Prerequisites:** ADR 0010 (evidence graph), ADR 0011 (captured declarations), ADR 0015 (one-hop change-impact review)
- **Architecture:** Approved Code Archeologist v0.2 — C# entry points, evidence-backed functionality map, honest limitations

## Problem

The existing snapshot graph identifies captured C# types, methods, source
invocations, and strictly provisional method-callee hypotheses. It does
not distinguish externally visible application entry points. To start a
functionality map, we need a narrowly safe inventory of source locations
that **look like** HTTP controller action annotations, without pretending
that source spelling establishes actual ASP.NET binding or runtime
route registration.

## Decision and representation

Implement `CSharpHttpAttributeInspector`, a bounded Roslyn **syntax-only**
pass over the same SHA-256-verified, saved C# source files as the other
graph analyzers. Its v1 scope is `MethodDeclarationSyntax` attributes
whose **unqualified simple syntax name** is exactly:

- `HttpGet`, `HttpPost`, `HttpPut`, `HttpPatch`, `HttpDelete`
- Or one of those spellings with `Attribute` appended.

The inspector deliberately does **not** resolve any attribute symbols,
imported namespaces, attribute aliases, controller inheritance,
application registration, source generators, configuration or middleware.
A custom attribute with the conventional same name may produce a
candidate even if it is unrelated to ASP.NET. Qualifying an attribute
name, using an alias or a different convention can cause omission.
Thus **every finding is a candidate, never an effective endpoint**.

Record for each observed attribute:

- Saved workspace/repository/snapshot and manifest fingerprint.
- HTTP verb inferred **from attribute spelling alone**.
- Attribute spelling, enclosing method identifier, exact method span.
- Exact attribute span, file path, file SHA-256 and one-based line/column.
- Separately observed raw string-literal positional route arguments on
  the HTTP attribute, a method `[Route]`, and its directly containing
  type `[Route]`, with per-argument status:
  `written-literal`, `omitted`, `absent`,
  `multiple-unresolved` or `nonliteral-or-unsupported`.

Only one ordinary positional, non-interpolated string literal of 256
characters or fewer is captured as a written route. Constants,
interpolation, `nameof`, named route arguments and ambiguous multiple
route attributes remain unresolved. These independent source strings
must **never** be composed into an effective URL or have
`[controller]`/`[action]` placeholders substituted in this slice.

## Graph and UI

Graph v6 (`csharp-snapshot-evidence-graph-v6`) joins each annotation
to its method declaration by **exact same file/path/SHA-256/span**,
cross-checking the separately computed syntax declaration report. A
missing/mismatched method fails HTTP 409; no imagined target is created.

A new graph node kind `http-endpoint-attribute` captures each written
annotation occurrence. An `annotates-method` edge of **status
`candidate`** points to the already indexed source method declaration.
The node label contains verb/attribute/method spelling, and the
detail distinguishes raw literal, absent and unsupported route pieces,
while explicitly refusing effective endpoint claims. Graph counts
`httpAttributeCandidateCount` and
`nonliteralHttpAttributeRouteCount` (plus analyzer version) are
derived from the independent saved-snapshot inspector.

The existing **C# evidence graph explorer** adds a clearly labeled
**Find HTTP attribute entry-point candidates** section, capped to the
first 30 rows, each showing the saved source address, candidate
classification, written route status and links to the observed method
and existing one-hop impact review. The node-kind filter also supports
the new category. An empty saved snapshot displays an explicit
limitation; **absence of candidates does not mean absence of endpoints**.

## Security and limits

- Verifies manifest and saved artifact hashes before consuming source.
- Maximum 250 C# files, 8 MiB combined C# source text, 3,000 observed
  HTTP verb annotations. Exceeding a limit fails HTTP 413 without a
  partial graph.
- Strict UTF-8 decoding; invalid text fails HTTP 422.
- Uses `CSharpSyntaxTree.ParseText`/syntax inspection only; does not
  load projects, invoke MSBuild, restore packages, run imported
  code, analyzers/source generators or import metadata references.
- No new endpoint, database migration, persistent map, package,
  model or GitHub CI workflow. Existing protected graph request,
  authorization, snapshot scope and hash checks apply.
- Default preprocessor parsing omits some conditional branches.
  Syntax errors can leave ambiguous/incomplete candidates.
- Minimal APIs (`MapGet`/`MapPost`), convention routes, `AcceptVerbs`,
  qualified/aliased attributes, dynamic route values, endpoint filters,
  auth policy, DI registration, application startup and actual URL
  reachability are **not covered**. A later implementation can add
  independently supported categories without upgrading these
  annotations to verified claims.

## Acceptance

- Disposable fixture with method `[HttpGet("items/{id}")]`,
  class `[Route("api/[controller]")]` and method
  `[HttpPost][Route("create")]` yields separate saved literal strings,
  with **no composed HTTP path**.
- `[HttpDelete(RouteToken)]` yields nonliteral/unsupported status, not
  a guessed route.
- `[CustomHttpGet]` and qualified
  `[global::HttpGet]` are not mistaken for supported conventional
  simple-name annotations.
- Every annotation edge has status `candidate`, exact saved file
  hash/source location and a preexisting observed C# method node.
- Graph counts, deterministic ordering, snapshot tamper handling,
  safe XML rejection, limits and restart stability remain valid.
- Windows `scripts/check.ps1` and browser acceptance are required
  before marking ready for owner squash merge.

This is a **source annotation map seed**, not a runtime route table
or end-to-end functionality graph.
