# Gate C2 — bounded evidence discovery

**Status:** Proposed implementation, pending owner-local GREEN and PR merge.

## Purpose

C2 introduces the first discoverable **application-owned built-in** read-only
evidence views for Gate C roles. C1's independent role/claim contract remains
unchanged. This milestone does **not** run agents, add model-callable tools,
publish an HTTP API, perform arbitrary repository reads, or alter the C# graph.

## Scope and authoritative source

`GateCEvidenceDiscovery.OpenVerified` receives a host-authorized
`SnapshotRecord`, a fixed `GateCScope`, a host-issued run ID and one
independent role. It calls existing `SnapshotEvidenceTools.ReadEntryPoint`
against the immutable saved snapshot before opening the role session.

Evidence is restricted to the verified `AgentEvidencePackage` for a supported
HTTP entry-point candidate. All relationships retain the original analyzer
status. Evidence already omitted/truncated by the underlying package remains
unavailable rather than being invented or retrieved from the live repository.

This is an initial catalog of **four implemented, versioned views**:

| Capability | Source |
| --- | --- |
| `inspect_entry_point` | Candidate HTTP entry-point relation |
| `inspect_direct_invocations` | Supported `contains-invocation` observed syntax |
| `inspect_candidate_callees` | Existing `candidate-callee` provisional-local relationships |
| `inspect_source_relationships` | Bounded relationships from the same verified evidence package |

The broader approved Gate C architecture also anticipates DI and richer
project-context tools. These are **not** claimed as implemented in C2 and
will require independently verified analyzer adapters and acceptance tests
before being advertised as available. Discoverable capabilities must match
the actual supported scope, not the aspirational complete catalog.

## Independent role and handle authority

Each role gets a separately instantiated `GateCEvidenceSession` for an exact
run and source scope. Neither Investigator nor Skeptic receives the other
role's request ledger or handle mapping. The host retains canonical source
IDs and can retrieve a copy via `HostCatalog`.

Agent-facing `GateCDiscoveryResponse` exposes short handles, source category,
original status, source location, bounded explanatory label and the exact
snapshot fingerprint. It does not expose canonical graph IDs.

Overlapping and repeated views reuse the same handle **within the same
session**. Two sessions may independently have an `E1` pointing to
different canonical evidence. `ValidateAssessment` binds the assessment to
the issuing run/role/scope and to the session's host catalog before delegating
to C1 validation. A bare handle string is never global authority.

## Resource and access limits

- Only allowlisted exact capability names are executable.
- Maximum 6 authorized requests per role session.
- Maximum 12 items returned per request.
- Maximum 48 distinct issued items per role session.
- Empty authorized calls still consume one request.
- Unauthorized tool names fail without accessing evidence.
- All returned items originate from the exact verified saved package.
- Rejected, cancelled or over-budget requests return explicit categories;
  they never synthesize substitute evidence.
- Unknown or inconsistent reference endpoints/statuses fail closed.
- No imported project execution, source generators, shell, external URLs,
  cloud AI, third-party plugins, or mutable source tree reads.

These bounds are initial deterministic C2 limits, subject to explicit
versioned configuration before the scored C6/C7 evaluation.

## Verification

Model-free tests are part of the existing `--gate-c1-self-test` mode invoked
from `scripts/check.ps1`. They check registry discovery, original status
preservation, independent role sessions, repeated handle identity, canonical
catalog compatibility, foreign assessment rejection, unauthorized names,
tool-call budget, cancellation, stale fingerprint and foreign snapshot.

The C2 test harness uses a synthetic in-memory `AgentEvidencePackage` with
exact known identities. It validates the bounded projection contract rather
than pretending to exercise the full on-disk source acquisition path.
The latter continues to be covered by the existing snapshot security suite
and by Gate B's real saved-source verification.

## Remaining milestones

C3 binds these host-controlled sessions to real independent MAF roles and
will require genuine local model verification. C4 adds Reviewer
reconciliation, C5 adds report UI, and C6/C7 run the approved comparative
evaluation. DI/project capabilities require separately scoped future adapters
rather than undocumented behavior.
