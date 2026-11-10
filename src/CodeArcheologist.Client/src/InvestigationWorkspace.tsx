import { useEffect, useRef, useState } from 'react';
import './InvestigationWorkspace.css';

type EntryPoint = { id: string; label: string; kind: string; filePath: string | null };
type Graph = { snapshotId: string; fingerprintSha256: string; nodes: EntryPoint[] };
type Scope = {
  workspaceId: string; repositoryId: string; snapshotId: string;
  fingerprintSha256: string; entryPointId: string; evidenceContractVersion: string;
};
type Summary = {
  runId: string; scope: Scope; reportVersion: string; status: string;
  diagnostic: string; startedAtUtc: string; finishedAtUtc: string | null;
};
type SummaryPage = { items: Summary[]; totalCount: number; hasMore: boolean };
type EvidenceLink = {
  role: number; handle: string; canonicalId: string; kind: number;
  sourceKind: string; evidenceStatus: string; filePath: string | null;
  fileSha256: string | null; line: number | null; column: number | null;
};
type Hypothesis = {
  id: string; text: string; evidenceHandles: string[]; uncertainty: string;
};
type Concern = {
  id: string; kind: number; explanation: string; evidenceHandles: string[];
};
type Assessment = {
  hypotheses: Hypothesis[]; concerns: Concern[]; unknowns: string[];
};
type RoleOutcome = {
  status: number; diagnostic: string; assessment: Assessment | null;
  selectedCapability: string | null; toolCallsUsed: number;
};
type ReviewDecision = {
  hypothesisId: string; recommendation: number; addressedConcernIds: string[];
  explanation: string;
};
type ReviewerOutcome = {
  status: number; diagnostic: string;
  assessment: {
    decisions: ReviewDecision[]; unresolvedGlobalConcernIds: string[];
  } | null;
  reconciliation: {
    check: { accepted: boolean; diagnostic: string };
    dispositions: { hypothesisId: string; recommendation: number; isVerifiedFact: boolean }[];
  } | null;
};
type Report = {
  version: string; runId: string; scope: Scope;
  investigatorModelId: string; skepticModelId: string; reviewerModelId: string;
  graphAnalyzerVersions: string[];
  progress: {
    status: number; investigatorStatus: number; skepticStatus: number;
    reviewerStatus: number;
  };
  startedAtUtc: string; finishedAtUtc: string;
  investigator: RoleOutcome; skeptic: RoleOutcome; reviewer: ReviewerOutcome;
  evidenceLinks: EvidenceLink[]; diagnostic: string;
};
type Detail = { summary: Summary; report: Report | null };
type EvidenceItem = {
  id: string; kind: string; evidenceStatus: string; filePath?: string | null;
  fileSha256?: string | null; evidencePath?: string; evidenceSha256?: string;
  line: number | null; column: number | null;
  fromId?: string; toId?: string;
};
type EvidencePackage = {
  contractVersion: string; snapshotId: string; fingerprintSha256: string;
  entryPointId: string; references: EvidenceItem[]; relations: EvidenceItem[];
};
type Verification = { state: 'verified'; match: EvidenceItem }
  | { state: 'unavailable'; reason: string };

const roles = ['Investigator', 'Skeptic', 'Reviewer'];
const roleStates = ['Not started', 'Running', 'Validated', 'Rejected', 'Failed', 'Timed out', 'Cancelled'];
const suggestions = ['Structurally supported', 'Plausible hypothesis', 'Contested / insufficient', 'Rejected'];
const concerns = ['Structural contradiction', 'Unsupported inference', 'Insufficient evidence', 'Competing hypothesis', 'Coverage limitation'];
const runStates = ['Created', 'Evidence verified', 'Independent assessment', 'Reconciliation', 'Completed', 'Incomplete', 'Cancelled', 'Rejected'];

function shortId(id: string) { return id.length > 18 ? id.slice(0, 18) + '…' : id; }
function when(value: string) { return new Date(value).toLocaleString(); }
function message(cause: unknown) { return cause instanceof Error ? cause.message : 'Unexpected local request failure'; }
async function requireOk(response: Response, fallback: string) {
  if (!response.ok) {
    const result: { error?: string } = await response.json().catch(() => ({}));
    throw new Error(result.error ?? fallback + ' (HTTP ' + response.status + ')');
  }
}
function isTerminal(status: string) { return status !== 'running'; }

function EvidenceReferences({ handles, role, links, verified, onVerify, onInspect }: {
  handles: string[]; role: number; links: EvidenceLink[];
  verified: Record<string, Verification>; onVerify: (link: EvidenceLink) => void;
  onInspect: (nodeId: string) => void;
}) {
  if (handles.length === 0) return <span className="investigation-muted">No cited source handle (declared evidence gap).</span>;
  return (
    <ul className="investigation-evidence-list">
      {handles.map((handle) => {
        const link = links.find(item => item.role === role && item.handle === handle);
        const key = role + ':' + handle;
        const check = verified[key];
        return (
          <li key={key}>
            {link ? (
              <>
                <button type="button" onClick={() => onVerify(link)} aria-label={'Verify ' + roles[role] + ' evidence ' + handle}>
                  {handle}: {link.sourceKind} · {link.evidenceStatus}
                </button>
                {link.filePath && <code>{link.filePath}{link.line === null ? '' : ':' + link.line}</code>}
                {!check && <small>Snapshot evidence not yet revalidated — select to verify.</small>}
                {check?.state === 'verified' && (
                  <>
                    <small role="status">Verified against original saved snapshot · SHA-256 {shortId(link.fileSha256 ?? '')}</small>
                    <button type="button" onClick={() => onInspect(check.match.fromId ?? check.match.id)}>
                      Inspect {check.match.fromId ? 'relation source' : 'evidence node'} in snapshot graph
                    </button>
                  </>
                )}
                {check?.state === 'unavailable' && (
                  <small role="alert">Cannot verify this link: {check.reason}</small>
                )}
              </>
            ) : (
              <span role="alert">Unresolved role-local handle: {handle}. Not a verified source link.</span>
            )}
          </li>
        );
      })}
    </ul>
  );
}

export function InvestigationWorkspace({ endpoint, workspaceId, repositoryId, snapshotId, fingerprintSha256, csrfToken, onInspectGraph }: {
  endpoint: string; workspaceId: string; repositoryId: string;
  snapshotId: string; fingerprintSha256: string; csrfToken: string;
  onInspectGraph: (nodeId: string) => void;
}) {
  const api = endpoint + '/investigations';
  const [entries, setEntries] = useState<EntryPoint[]>([]);
  const [selectedEntry, setSelectedEntry] = useState('');
  const [list, setList] = useState<SummaryPage | null>(null);
  const [detail, setDetail] = useState<Detail | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [verification, setVerification] = useState<Record<string, Verification>>({});
  const [verifying, setVerifying] = useState('');
  const key = useRef<string | null>(null);
  const lastEntry = useRef('');
  const activeRun = detail?.summary.status === 'running' ? detail.summary.runId : null;

  useEffect(() => {
    const controller = new AbortController();
    setError(''); setDetail(null); setVerification({}); setLoading(true);
    async function load() {
      try {
        const [graphResponse, runsResponse] = await Promise.all([
          fetch(endpoint + '/csharp-evidence-graph', { credentials: 'same-origin', cache: 'no-store', signal: controller.signal }),
          fetch(api, { credentials: 'same-origin', cache: 'no-store', signal: controller.signal }),
        ]);
        await requireOk(graphResponse, 'Cannot verify saved evidence graph');
        await requireOk(runsResponse, 'Cannot load investigation history');
        const graph: Graph = await graphResponse.json();
        const history: SummaryPage = await runsResponse.json();
        if (graph.snapshotId !== snapshotId || graph.fingerprintSha256 !== fingerprintSha256)
          throw new Error('Snapshot identity mismatch. Investigation controls are disabled.');
        if (!history.items.every(item =>
          item.scope.workspaceId === workspaceId &&
          item.scope.repositoryId === repositoryId &&
          item.scope.snapshotId === snapshotId &&
          item.scope.fingerprintSha256 === fingerprintSha256))
          throw new Error('Investigation history contains a foreign snapshot scope.');
        if (!controller.signal.aborted) {
          const eligible = graph.nodes.filter(node =>
            node.kind === 'http-endpoint-attribute' || node.kind === 'http-minimal-mapping');
          setEntries(eligible);
          setSelectedEntry(eligible[0]?.id ?? '');
          setList(history);
        }
      } catch (cause) {
        if (!controller.signal.aborted) setError(message(cause));
      } finally {
        if (!controller.signal.aborted) setLoading(false);
      }
    }
    void load();
    return () => controller.abort();
  }, [endpoint, api, snapshotId, fingerprintSha256, workspaceId, repositoryId]);

  async function refreshList() {
    const response = await fetch(api, { credentials: 'same-origin', cache: 'no-store' });
    await requireOk(response, 'Cannot refresh investigation history');
    const page: SummaryPage = await response.json();
    if (!page.items.every(item => matchesScope(item.scope)))
      throw new Error('History response does not belong to this snapshot.');
    setList(page);
  }

  function matchesScope(scope: Scope) {
    return scope.workspaceId === workspaceId &&
      scope.repositoryId === repositoryId && scope.snapshotId === snapshotId &&
      scope.fingerprintSha256 === fingerprintSha256;
  }

  async function openRun(runId: string, signal?: AbortSignal): Promise<Detail> {
    const response = await fetch(api + '/' + encodeURIComponent(runId), {
      credentials: 'same-origin', cache: 'no-store', signal,
    });
    await requireOk(response, 'Unable to reopen investigation');
    const value: Detail = await response.json();
    if (value.summary.runId !== runId || !matchesScope(value.summary.scope) ||
        value.report && (value.report.runId !== runId || !matchesScope(value.report.scope) ||
          value.report.version !== value.summary.reportVersion ||
          value.report.progress.status !== runStates.findIndex(s => s.toLowerCase() === value.summary.status)))
      throw new Error('Investigation report has inconsistent provenance or status.');
    if (!signal?.aborted) { setDetail(value); setVerification({}); }
    return value;
  }

  useEffect(() => {
    if (!activeRun) return;
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    async function poll() {
      try {
        const current = await openRun(activeRun!, controller.signal);
        if (controller.signal.aborted) return;
        if (isTerminal(current.summary.status)) {
          await refreshList();
          return;
        }
        timer = setTimeout(() => void poll(), 2500);
      } catch (cause) {
        if (!controller.signal.aborted) setError('Status polling stopped: ' + message(cause) +
          '. Reopen the run to check its persisted state.');
      }
    }
    timer = setTimeout(() => void poll(), 2500);
    return () => { controller.abort(); if (timer) clearTimeout(timer); };
  // Only restart polling when the active run identity changes.
  }, [activeRun]);

  async function startRun() {
    if (!selectedEntry || busy) return;
    if (selectedEntry !== lastEntry.current) key.current = null;
    lastEntry.current = selectedEntry;
    key.current ??= crypto.randomUUID();
    setBusy(true); setError(''); setNotice('');
    try {
      const response = await fetch(api, {
        method: 'POST', credentials: 'same-origin',
        headers: {
          'Content-Type': 'application/json',
          'X-CodeArcheologist-Request': '1',
          'X-CodeArcheologist-Csrf': csrfToken,
          'Idempotency-Key': key.current,
        },
        body: JSON.stringify({ entryPointId: selectedEntry }),
      });
      await requireOk(response, 'Investigation could not be started');
      const result: Summary = await response.json();
      if (!matchesScope(result.scope) || result.scope.entryPointId !== selectedEntry)
        throw new Error('Start response does not match the selected snapshot and entry point.');
      key.current = null;
      await openRun(result.runId);
      await refreshList();
      setNotice(response.status === 202
        ? 'Investigation queued locally. This browser can be closed; restart does not resume unfinished runs.'
        : 'Reopened an existing idempotent investigation.');
    } catch (cause) { setError(message(cause)); }
    finally { setBusy(false); }
  }

  async function verifyEvidence(link: EvidenceLink) {
    const keyValue = link.role + ':' + link.handle;
    if (verifying) return;
    setVerifying(keyValue);
    try {
      if (!detail?.report) throw new Error('No saved report available.');
      const response = await fetch(
        endpoint + '/evidence-tools/entry-point?entryPointId=' +
          encodeURIComponent(detail.report.scope.entryPointId),
        { credentials: 'same-origin', cache: 'no-store' });
      await requireOk(response, 'Saved source evidence is not available');
      const packageResult: EvidencePackage = await response.json();
      if (packageResult.contractVersion !== detail.report.scope.evidenceContractVersion ||
          packageResult.snapshotId !== snapshotId ||
          packageResult.fingerprintSha256 !== fingerprintSha256 ||
          packageResult.entryPointId !== detail.report.scope.entryPointId)
        throw new Error('Saved source package identity does not match this report.');
      const match = [...packageResult.references, ...packageResult.relations]
        .find(item => item.id === link.canonicalId);
      const filePath = match?.filePath ?? match?.evidencePath ?? null;
      const hash = match?.fileSha256 ?? match?.evidenceSha256 ?? null;
      if (!match || match.kind !== link.sourceKind ||
          match.evidenceStatus !== link.evidenceStatus ||
          filePath !== link.filePath || hash !== link.fileSha256 ||
          match.line !== link.line || match.column !== link.column)
        throw new Error('Evidence identity or saved source location no longer matches.');
      setVerification(current => ({ ...current, [keyValue]: { state: 'verified', match } }));
    } catch (cause) {
      setVerification(current => ({
        ...current, [keyValue]: { state: 'unavailable', reason: message(cause) },
      }));
    } finally { setVerifying(''); }
  }

  const report = detail?.report;
  return (
    <section className="investigation-workspace" aria-label="Evidence-linked investigation workspace">
      <header className="investigation-header">
        <div>
          <span className="investigation-eyebrow">GATE C5 / LOCAL INVESTIGATION</span>
          <h4>Investigate a captured HTTP candidate</h4>
          <p className="workspace-note">Investigators propose unverified interpretations; the Skeptic challenges them; the Reviewer reconciles objections. No agent establishes runtime truth.</p>
        </div>
        <code title={fingerprintSha256}>Snapshot SHA-256 {shortId(fingerprintSha256)}</code>
      </header>
      {loading ? <p>Verifying saved snapshot and investigation history…</p> : (
        <>
          {entries.length === 0 && !error && <p role="status">No supported HTTP candidates in this snapshot. An investigation cannot be started here.</p>}
          <div className="investigation-start">
            <label htmlFor="investigation-entry">Saved HTTP candidate</label>
            <select id="investigation-entry" value={selectedEntry}
              disabled={busy || entries.length === 0 || activeRun !== null}
              onChange={event => { setSelectedEntry(event.target.value); key.current = null; }}>
              {entries.map(entry => (
                <option key={entry.id} value={entry.id}>{entry.label} · {entry.filePath ?? 'source unavailable'}</option>
              ))}
            </select>
            <button type="button" disabled={!selectedEntry || busy || activeRun !== null}
              onClick={() => void startRun()}>{busy ? 'Starting…' : 'Start investigation (local Ollama)'}</button>
            <button type="button" disabled={busy} onClick={() => void refreshList().catch(cause => setError(message(cause)))}>Refresh history</button>
          </div>
          {notice && <p role="status" className="registration-notice">{notice}</p>}
          {error && <p role="alert" className="error">{error}</p>}
          <div className="investigation-layout">
            <aside className="investigation-history">
              <h5>Saved investigations</h5>
              {list?.items.length === 0 && <p>No investigations for this snapshot yet.</p>}
              <ul>{list?.items.map(item => (
                <li key={item.runId}>
                  <button type="button" className={detail?.summary.runId === item.runId ? 'investigation-selected' : ''}
                    aria-pressed={detail?.summary.runId === item.runId}
                    onClick={() => { setError(''); void openRun(item.runId).catch(cause => setError(message(cause))); }}>
                    <strong>{item.status}</strong>
                    <small>{when(item.startedAtUtc)} · {shortId(item.scope.entryPointId)}</small>
                  </button>
                </li>
              ))}</ul>
              {list?.hasMore && <p className="workspace-note">Showing {list.items.length} of {list.totalCount} saved runs.</p>}
            </aside>
            <div className="investigation-detail">
              {!detail && <p role="status">Choose a saved investigation or start a new one.</p>}
              {detail && (
                <>
                  <div className="investigation-status">
                    <div><span className="investigation-eyebrow">RUN {shortId(detail.summary.runId)}</span>
                      <h5>{detail.summary.status.toUpperCase()}</h5></div>
                    <p>{detail.summary.diagnostic}</p>
                    <small>Started {when(detail.summary.startedAtUtc)}
                      {detail.summary.finishedAtUtc && ' · Finished ' + when(detail.summary.finishedAtUtc)}</small>
                    {detail.summary.status === 'running' && <p role="status">Local agents are working. Status refreshes approximately every 2.5 seconds. This does not imply progress through all three roles.</p>}
                  </div>
                  {!report && detail.summary.status !== 'running' &&
                    <p role="status">This run ended without a validated report. The ledger preserves its status and diagnostic; no missing assessment is invented.</p>}
                  {report && (
                    <>
                      <p className="workspace-note">Report contract {report.version} · Analyzer {report.graphAnalyzerVersions.join(', ') || 'unavailable'} · Ollama {report.investigatorModelId}. These are execution provenance, not quality scores.</p>
                      <ol className="investigation-timeline">
                        {roles.map((role, i) => (
                          <li key={role}><strong>{role}</strong>
                            <span>{roleStates[[report.progress.investigatorStatus, report.progress.skepticStatus, report.progress.reviewerStatus][i]] ?? 'Unknown'}</span>
                          </li>
                        ))}
                      </ol>
                      <div className="investigation-roles">
                        {([['Investigator', report.investigator, 0], ['Skeptic', report.skeptic, 1]] as const).map(([role, outcome, roleIndex]) => (
                          <section key={role} className="investigation-role">
                            <h5>{role} <small>{roleStates[outcome.status] ?? 'Unknown'}</small></h5>
                            <p className="workspace-note">{outcome.diagnostic} · Evidence view: {outcome.selectedCapability ?? 'none'}</p>
                            {roleIndex === 0 && outcome.assessment?.hypotheses.map(h => (
                              <article key={h.id} className="investigation-finding">
                                <span className="investigation-eyebrow">UNVERIFIED HYPOTHESIS · {h.id}</span>
                                <p>{h.text}</p><p><strong>Unknown:</strong> {h.uncertainty}</p>
                                <EvidenceReferences handles={h.evidenceHandles} role={roleIndex}
                                  links={report.evidenceLinks} verified={verification} onVerify={link => void verifyEvidence(link)}
                                  onInspect={onInspectGraph} />
                              </article>
                            ))}
                            {roleIndex === 1 && outcome.assessment?.concerns.map(h => (
                              <article key={h.id} className="investigation-finding">
                                <span className="investigation-eyebrow">SKEPTIC OBJECTION · {h.id} · {concerns[h.kind] ?? 'Unknown'}</span>
                                <p>{h.explanation}</p>
                                <EvidenceReferences handles={h.evidenceHandles} role={roleIndex}
                                  links={report.evidenceLinks} verified={verification} onVerify={link => void verifyEvidence(link)} />
                              </article>
                            ))}
                            {outcome.assessment && (
                              <details><summary>Unresolved questions ({outcome.assessment.unknowns.length})</summary>
                                <ul>{outcome.assessment.unknowns.map((u, i) => <li key={i}>{u}</li>)}</ul>
                              </details>
                            )}
                          </section>
                        ))}
                        <section className="investigation-role">
                          <h5>Reviewer <small>{roleStates[report.reviewer.status] ?? 'Unknown'}</small></h5>
                          <p className="workspace-note">{report.reviewer.diagnostic}</p>
                          {report.reviewer.assessment?.decisions.map(d => (
                            <article className="investigation-finding" key={d.hypothesisId}>
                              <span className="investigation-eyebrow">{d.hypothesisId} · {suggestions[d.recommendation] ?? 'Unclassified'}</span>
                              <p>{d.explanation}</p>
                              <p>Addressed objections: {d.addressedConcernIds.join(', ') || 'none'}</p>
                            </article>
                          ))}
                          {report.reviewer.assessment && <p>Unresolved global objections: {report.reviewer.assessment.unresolvedGlobalConcernIds.join(', ') || 'none'}</p>}
                          <p className="workspace-note">
                            Host validation: {report.reviewer.reconciliation?.check.accepted
                              ? 'Accepted as a structurally valid reconciliation (not semantic proof).'
                              : 'No accepted host reconciliation.'}
                          </p>
                        </section>
                      </div>
                      {verifying && <p role="status">Revalidating saved source evidence…</p>}
                      <p className="workspace-note">Evidence navigation never opens the mutable source repository. Selecting a handle checks its canonical identity, hash and location against the original snapshot evidence package; an unavailable target stays unavailable.</p>
                    </>
                  )}
                </>
              )}
            </div>
          </div>
        </>
      )}
    </section>
  );
}
