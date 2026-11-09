# ADR 0008 — Static C# project membership candidates, no MSBuild evaluation

- **Date:** 2026-11-09
- **Status:** Draft implementation; owner-run Windows verification pending
- **Prerequisites:** ADRs 0004–0007, particularly hash-verified snapshot artifacts
- **Architecture:** Approved Code Archeologist v0.2, ingestion project-file identification and trustworthy relationship labels

## Context

Our PR #9 reference-free synthetic Roslyn compilation deliberately pooled
all captured `.cs` files into one compilation. It cannot tell whether two
files belong in the same actual project. Evaluating imported MSBuild projects
with ordinary project loaders would run untrusted targets or load unapproved
imports. We need a smaller, safe step before project-aware bindings.

## Decision and response contract

Add an authenticated read-only endpoint scoped to one stored snapshot:

`GET /api/v1/workspaces/{w}/repositories/{r}/snapshots/{s}/csharp-projects`

The route verifies the snapshot's manifest and **every saved artifact hash**,
then reads `.csproj` bytes a second time from the application-managed saved
copy with per-file hash validation. No mutable repository path is used.

For each project, return its exact snapshot path and SHA-256, declared
`Sdk` string, whether an SDK default `Compile` pattern was inferred,
candidate C# source files with exact snapshot paths/hashes and their
`inclusionBasis`, plus **unresolved rules**. The report includes snapshot,
repository and workspace IDs, analyzer version and global coverage limits.

The only first-slice candidate mechanisms are:

1. For a small explicit set of recognized `Microsoft.NET.Sdk` variants,
   approximate the default `**/*.cs` pattern within the project directory
   if unconditional `EnableDefaultCompileItems` and `EnableDefaultItems`
   settings do not disable it. A candidate matched this way is marked
   `sdk-default-pattern`.
2. For a literal, unconditional `Compile Include="relative/path.cs"`
   inside a direct, unconditional `ItemGroup`, normalize the virtual
   relative path without disk access, reject paths outside the captured
   snapshot, and mark `literal-compile-include`. Relative `../`
   references **within** the captured root are supported.
3. Apply unconditional literal `Compile Remove` entries to the candidate
   set. Do not pretend wildcard and property-expression rules were applied.

All resulting membership is **candidate-only**. The report also lists C#
files without a static project candidate. Files may appear as candidates
in multiple projects; this is not proof of actual compilation membership.

## Fail-closed XML and source handling

- Never invoke MSBuild, project evaluation, imported tasks/targets, restore,
  Git, source generators, analyzers, compilation, shell or arbitrary code.
- Parse XML with `XmlReaderSettings.DtdProcessing = Prohibit`,
  `XmlResolver = null`, an XML document size limit and strict UTF-8.
  DTDs and malformed XML yield HTTP 422 rather than external-entity reads.
- Limit inspections to 100 `.csproj` files, 2 MiB per XML and 8 MiB total
  project XML. Excess yields HTTP 413, not a partially successful report.
- Missing or tampered saved artifacts yield HTTP 409. Unknown snapshot
  scope yields 404, unauthenticated callers 401.
- Data stays local in memory: no database migration, external service,
  model invocation or new runtime dependency.

## Explicit unknowns

This static inspection is **not MSBuild evaluation**. Real inclusion can
depend on imported `Directory.Build.props/targets`, SDK defaults,
`DefaultItemExcludes`, globs, `Condition`, `Choose`, target frameworks,
generated files, linked files, property expansion, and project references.
The detector logs unresolved conditional/wildcard/property rules; it does
not execute or synthesize their outcome.

As a result, the presence of both endpoints and types in the same candidate
project must not be promoted to verified C# binding, dependency graph edge,
or effective build membership. PR #9's provisional links are **unchanged**.
A future analysis may incorporate static project candidates as supporting
evidence, but cannot use them alone to certify a resolved relationship.

## Tests

Disposable, source-nonexecuting HTTP integration fixtures demonstrate:

- An SDK project with default source matches, unconditional `Compile Remove`,
  and a literal cross-directory `Compile Include` within snapshot bounds;
- A second project disabling defaults and including one explicit file;
- Conditional and wildcard rules left unresolved; an old-style project
  without observed Compile items generates no inferred members;
- Exact file and project SHA-256 anchors, stable ordering, authentication,
  scope 404, saved-file tamper rejection and unchanged results after a
  live source edit and API restart;
- DTD/external entity source rejected with 422; the snapshot itself remains
  inspectable.

Passing this milestone is **not** evidence that the project builds or its
references are resolved. Nonexecuting, project-aware symbol compilation
remains a future, separately gated design problem.
