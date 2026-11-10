# Gate C5b — Evidence-linked Investigation Workspace

**Status:** Pending owner-local GREEN, visual review, and a real-Ollama end-to-end investigation.
**Base:** merged C5a (PR #31).

## Experience

Open a registered workspace, choose a repository, then expand its **Read-only snapshots**. For any saved snapshot, select **Open AI investigation workspace**.

The workspace offers a bounded picker of source-derived HTTP candidates from the existing verified graph, a **Start investigation (local Ollama)** action, recent saved run history, and a detail view for revisiting reports after refresh or process restart. The host enforces single-run concurrency and the time limit; the browser only polls the persisted summary, never owns the MAF execution lifetime.

For a completed report, three role sections display the Investigator's hypotheses and explicitly named unknowns; the Skeptic's independent concerns and unknowns; and the Reviewer's reconciliation with linked/remaining objections. The terminal status, per-role workflow statuses, safe diagnostics, selected evidence capability, model and analyzer versions are visible. A completed run is **a validated workflow**, not independent semantic, compiler or runtime proof.

## Evidence navigation trust boundary

Agent-issued short handles are scoped to a particular role, run and snapshot. The user selects a cited handle to **verify against the saved snapshot evidence tool** before graph navigation becomes available. The UI checks the returned package against the report's snapshot ID, fingerprint, evidence-tool contract version and entry-point ID. It then checks the cited host-owned canonical ID, evidence kind/status, saved path, SHA-256 and exact line/column. A missing, changed or inaccessible source is displayed as **unavailable**, not silently linked to newer mutable working-tree evidence.

Only after successful revalidation is a graph-navigation button enabled. An evidence node opens its saved graph node; a relationship opens the relationship's recorded source node for further inspection. The existing graph view independently rejects node IDs absent from that saved graph. The UI does **not** execute source code or open arbitrary paths.

## C5b limitations

- Running status is intentionally coarse: the C5a API does not expose incremental role events. A `running` label does not imply that any particular role has completed.
- History is limited to the 50 newest records returned by the current C5a API, with a count indicator when older records exist. No unbounded fetch.
- The verified graph view may show relationships as *candidate* or *provisional* only. The presence of a citation does not elevate the claim to a fact.
- Graph navigation highlights a source node, not an IDE file browser or line-number deep link.
- No model prompt changes, new privilege, arbitrary script execution, persistence migration, CI enabling, or cross-repository analysis.

## Verification

Run `.\\scripts\\check.ps1` (or `./scripts/check.sh` on a supported development system) from the feature branch; do not merge if the build, TypeScript typecheck, bundling, model-free or local API/security/snapshot tests fail.

Run the real `--gate-c4-run 3` probe to check the existing sequential MAF Investigator/Skeptic/Reviewer contracts. Also manually exercise the C5b interface with a real saved HTTP candidate and local Ollama:

1. Open a saved snapshot, open its AI investigation workspace, and pick an eligible HTTP candidate.
2. Start a run; confirm the status remains `running` until the persisted backend reaches a terminal state.
3. Reopen the saved run; confirm the scope, role statuses, contested/unresolved Reviewer results, hypotheses, concerns and limitations are visible.
4. Select at least one Investigator and one Skeptic citation, check that the snapshot evidence revalidates, and navigate to the graph. Verify that evidence statuses remain candidate/provisional where appropriate.
5. Restart the API during another run if practical; the ledger should mark that run incomplete, never fabricate a completed report. If the source evidence is unavailable/tampered, navigation must refuse to claim verification.

**Success is not just green TypeScript:** the UI must work end to end against actual saved evidence and real local agents. Document any issue and fix it on the same draft PR before review/merge.
