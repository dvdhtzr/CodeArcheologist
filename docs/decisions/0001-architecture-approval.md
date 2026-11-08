# ADR 0001: Owner approval and architectural baseline

**Status:** Approved direction; validation gates remain open
**Date:** November 8, 2026
**Decision owner:** Project owner

## Decision

The owner explicitly approved Code Archeologist's v0.2 architectural direction and authorized Phase 1 implementation. The original v0.1 and v0.2 design documents are preserved unchanged, including the then-pending approval wording; this subsequent record supersedes their historical approval status. It does not silently override their security, integrity, uncertainty, scope, or evaluation constraints.

The following technical defaults are ratified as architectural directions:

| ID | Direction | Remaining validation |
|---|---|---|
| ADR-001 | SQLite with durable evidence/typed graph and FTS5 | WAL, backup, migration, native packaging |
| ADR-002 | One Obsidian-compatible vault per workspace | protected edits, rename/conflict/restore |
| ADR-003 | Localhost-first, single-user host | authenticated browser and origin safeguards |
| ADR-004 | Pluggable embedded vectors | benchmark before selecting engine |
| ADR-005 | Durable SSE events plus HTTP user commands | replay and reconnect |
| ADR-006 | Application-owned state with MAF checkpoint adapter | recovery and SDK compatibility |
| ADR-007 | Non-executing snapshot-first analyzers | adversarial Roslyn/TypeScript validation |
| ADR-008 | Trusted built-ins and consent-gated third-party plugins | subprocess privilege controls, independent packaging |
| ADR-009 | Windows x64, Linux x64, macOS ARM64/x64 as validation targets | representative clean-machine tests |
| ADR-010 | Apache-2.0 and DCO sign-off | dependency licensing audit |
| ADR-011 | Immutable revision-anchored source and evidence | cross-revision and cross-repo fixtures |
| ADR-012 | Deterministic independently evaluated quality gates | benchmark-calibrated numeric thresholds |

## Unresolved risks, not guarantees

This approval is permission to implement and test, **not** evidence that any selected technology works, that untrusted imported repositories can be analyzed safely, or that third-party plugins have OS sandbox isolation. No user source execution is permitted without a future approved secure sandbox; in-process and subprocess plugin execution must not be described as sandboxed without demonstrated containment. Critical integrity failures prohibit a successful or deceptively qualified conclusion. Numeric quality thresholds and the precise vector engine remain deliberately unset pending measured evaluation.

## Implementation consequence

Build in small topic-branch PRs, with owner-approved merges, local verification, and **no paid CI**. Sensitive APIs must not exist before local browser authentication, host/origin and CSRF protections have been implemented and tested. Public previews are gated by the strict original design invariants.
