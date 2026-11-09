# ADR 0009 — Cross-check provisional type links with project membership candidates

- **Date:** 2026-11-09
- **Status:** Draft implementation; awaiting owner-run Windows and browser acceptance
- **Prerequisites:** ADR 0004 (hash-verified snapshots), ADR 0007 (provisional Roslyn links), ADR 0008 (static project candidates)
- **Architecture:** Code Archeologist v0.2 — evidence integrity, trust-qualified structure, no fabricated dependencies

## Problem

The existing Roslyn type-link analyzer constructs a reference-free synthetic
compilation from all saved C# files. That can suggest a binding across files
which never belong to the same actual project. The existing static .csproj
inspector proposes source-file membership candidates but never evaluates
MSBuild or proves effective compiler inputs.

We need to expose the **intersection and disagreements** between these two
evidence sources without silently raising confidence.

## Decision

Add one read-only, authenticated snapshot-scoped endpoint:

`GET /api/v1/workspaces/{w}/repositories/{r}/snapshots/{s}/csharp-link-context`

It invokes the two existing analyzers for the **same** stored snapshot and
requires matching workspace, repository, snapshot and fingerprint provenance.
Each analyzer already verifies the manifest and captured file bytes.
No new project evaluation, Roslyn compilation, source import, database table,
remote API, plugin, model or build execution is added in the joining layer.

For each source-local type usage, preserve the **entire original type-link
observation** (including `provisional-local` or `unresolved`), and attach:
- Candidate project membership for the source file, with project path,
  SHA-256, inclusion reason and the number of unresolved project rules.
- Candidate project membership for the source declaration target, if one exists.
- The intersection of source and target candidate project paths/hashes,
  when such a candidate overlap exists.
- One explicit **project-context classification**, with a natural-language
  explanation.

The classifications are:

| Project context status | Meaning | Confidence |
| --- | --- | --- |
| `shared-project-candidate` | Source and candidate target appear under the same *static* project hypothesis | **Provisional only** |
| `disjoint-project-candidates` | Source/target have candidates, but their candidate project sets do not overlap | **Not proof of invalid binding** |
| `unknown-project-candidate` | Source or target has no static project candidate | **Unknown** |
| `unresolved-target` | The earlier Roslyn analysis could not bind a unique captured source declaration | **Unresolved** |

**None** of these statuses is `verified`, `compiled`, `certain`,
`real-dependency` or `invalid`. If two files appear in the same candidate
project, the relationship is still merely a hypothesis. A disjoint candidate
classification does **not** prove a cross-project relationship is invalid;
the actual solution might use project references, linked files or imported
conditions we do not evaluate.

## Provenance and failure behavior

The report includes exact workspace/repository/snapshot IDs, original snapshot
fingerprint, joiner version and each upstream analyzer's version. Source usage
and candidate target remain anchored to the **captured file paths, SHA-256 and
line positions**. Projects are anchored to exact captured project file hashes.
Results are computed on demand, not written to SQLite.

Upstream limits propagate: 250 C# files / 8 MiB and 6,000 usages for type
links; 100 projects / 2 MiB per XML and 8 MiB total project XML for static
project inspection. An exceeded bound returns 413 with no partial joined
success. Invalid UTF-8 or unsafe DTD XML returns 422. Manifest or captured
content tampering returns 409. Invalid snapshot scoping returns 404 and a
missing authenticated browser session returns 401.

## Explicit limitations

- Neither upstream analyzer evaluates project conditions, SDK imports,
  `Directory.Build.props`, generated source, NuGet, references or effective
  compiler inputs.
- The reference-free synthetic Roslyn compilation can incorrectly bind
  types from independent projects into a single imaginary compilation.
- Default include candidates may overinclude nested project directories;
  missing candidate membership cannot be interpreted as exclusion.
- Only base-type clauses and explicit object creation types have upstream
  syntactic links. There is still no call graph or reliable change impact
  analysis.
- No imported code is executed and no original registered repository path
  is reopened during inspection.
- Saved snapshot provenance is immutable-by-application, not a proof of an
  authentic signed source revision or an OS-enforced tamper-resistant store.

## Acceptance

A disposable repository fixture must demonstrate each of the four
classifications, with saved source/target/project hashes and unchanged
provisional upstream status. Integration tests cover exact scope, anonymous
denial, bounded input, unsafe project XML, saved-byte tamper rejection,
live-working-tree changes and API restart reconstruction.

The browser's **Inspect link context** screen must present each status and
the reason, plus source/target/project evidence and limitations. No automatic
confidence promotion, verified edge or graph persistence is authorized.

Project-aware, non-executing compilation with trusted reference metadata,
deduplicated symbols and a durable typed graph require separate design and
verification.
