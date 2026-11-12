import { useEffect, useState } from 'react';
import type { AdaptiveActivity, ApiClient, Scope } from './api.ts';

const CAPABILITIES: Record<string, string> = {
  inspect_entry_point: 'Entry-point source',
  inspect_direct_invocations: 'Direct invocations',
  inspect_candidate_callees: 'Candidate callees',
  inspect_source_relationships: 'Source relationships',
};

const display = (value: string | null): string =>
  value === null ? 'Not durably recorded' : value.replaceAll('-', ' ');

/**
 * Purely read-only. The durable REST projection is authoritative; SignalR
 * activity only schedules a new read. No client-side replay or inference.
 */
export function AdaptiveEvidenceActivity({ api, scope, runId, revision }: {
  api: ApiClient; scope: Scope; runId: string; revision: number;
}) {
  const [data, setData] = useState<AdaptiveActivity | null>(null);
  const [mode, setMode] = useState<'loading' | 'stable' | 'adaptive' | 'error'>('loading');
  const [error, setError] = useState<string | null>(null);
  const [refresh, setRefresh] = useState(0);

  useEffect(() => {
    let active = true;
    setMode('loading');
    setData(null);
    setError(null);
    void (async () => {
      try {
        const context = await api.adaptiveContext(scope, runId);
        if (!active) return;
        if (context.status === 'not-adaptive') { setMode('stable'); return; }
        if (context.status !== 'verified' || context.context?.runId !== runId ||
            context.context.scope.snapshotId !== scope.snapshotId)
          throw new Error('Adaptive context identity could not be verified.');
        const activity = await api.adaptiveActivity(scope, runId);
        if (!active) return;
        if (activity.projectionVersion !== 'r3-adaptive-evidence-activity-v1' ||
            activity.runId !== runId || activity.snapshotId !== scope.snapshotId)
          throw new Error('Unexpected adaptive activity version or snapshot scope.');
        setData(activity);
        setMode('adaptive');
      } catch (failure) {
        if (!active) return;
        setMode('error');
        setError(failure instanceof Error ? failure.message : 'Adaptive history unavailable.');
      }
    })();
    return () => { active = false; };
  }, [api, scope.workspaceId, scope.repositoryId, scope.snapshotId, runId, revision, refresh]);

  if (mode === 'stable') return null;
  return <section className="recovery-status" aria-label="Adaptive Evidence Activity">
    <div className="live-inline">
      <strong>Adaptive Evidence Activity</strong>
      <button type="button" className="text-button" onClick={() => setRefresh(n => n + 1)}>
        Refresh verified history
      </button>
    </div>
    {mode === 'loading' && <p>Verifying saved adaptive evidence history…</p>}
    {mode === 'error' && <p className="live-error" role="alert">
      Adaptive history integrity or availability failure: {error}. No evidence is presented as verified.
    </p>}
    {mode === 'adaptive' && data && <>
      <small>{data.note}</small>
      {data.attempts.length === 0 && <p>No recorded execution attempts yet.</p>}
      {data.attempts.map(attempt => <details key={attempt.attemptId} open>
        <summary>Attempt {attempt.number} · {attempt.status}
          {attempt.interrupted ? ' · interrupted or incomplete' : ''}</summary>
        {attempt.roles.map(role => <div key={role.role} className="recovery-status">
          <strong>{role.role === 'investigator' ? 'Investigator' : 'Skeptic'} ·
            {role.historyStatus === 'verified' ? ' verified host history' : ' history unavailable / unrecorded'}
          </strong>
          {role.requests.length > 0
            ? <ul>{role.requests.map(request => <li key={request.receiptId}>
              Host-issued #{request.ordinal} {CAPABILITIES[request.capability] ?? request.capability}
              {' '}({request.phase}) · {request.evidenceCount} total handles;
              {' '}{request.newItemCount} new · receipt {request.receiptId.slice(0, 8)}
            </li>)}</ul>
            : <p>No persisted requests for this role in this attempt.</p>}
          <p><strong>Model proposal:</strong> {role.decision === 'unrecorded'
            ? 'Unrecorded / unknown (not evidence of “none”)'
            : display(role.proposedGapKind)}</p>
          {role.decision !== 'unrecorded' && <p>
            <strong>Host-mapped request:</strong> {role.requestedCapability
              ? CAPABILITIES[role.requestedCapability] ?? role.requestedCapability : 'Not recorded'}
            {' '}· <strong>Host decision:</strong> {display(role.hostDecision)}
            {' '}· <strong>Additional evidence:</strong> {display(role.evidenceOutcome)}
          </p>}
          <p><strong>Recorded stopping reason:</strong> {display(role.stopReason)}
            {' '}· <strong>Checkpoint:</strong> {role.checkpointReused
              ? 'revalidated and reused' : role.checkpointAccepted
                ? 'accepted in this attempt' : 'no accepted checkpoint recorded'}
          </p>
        </div>)}
      </details>)}
    </>}
  </section>;
}
