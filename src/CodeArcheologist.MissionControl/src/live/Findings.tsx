import { useEffect, useState } from 'react';
import type { ApiClient, Scope, SourceView } from './api.ts';
import {
  CONCERN_KINDS, HOST_CAUTION_LABELS, ROLE_STATUS, RUN_STATUS, VERDICTS,
  citations, hostCautionView, hostSourceCitation, label, supportedReportVersion, verdicts,
} from './report.ts';
import type { Citation, HostSyntaxCaution, Report, RunDetail } from './report.ts';

/**
 * MC4 findings view of the host-recorded C5 report. AI-written text is shown
 * as plain text (React escapes it; no Markdown/HTML rendering) and labelled.
 * Citations open bounded lines from the saved, hash-verified snapshot.
 */
export function Findings({ api, scope, runId, runStatus }: {
  api: ApiClient; scope: Scope; runId: string; runStatus: string | undefined;
}) {
  const [detail, setDetail] = useState<RunDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [source, setSource] = useState<{ citation: Citation; view: SourceView | null; error: string | null } | null>(null);

  useEffect(() => {
    let current = true;
    setError(null);
    setDetail(null);
    setSource(null);
    api.run<RunDetail>(scope, runId)
      .then(result => { if (current) setDetail(result); })
      .catch((e: unknown) => { if (current) setError(e instanceof Error ? e.message : String(e)); });
    return () => { current = false; };
  }, [api, scope.workspaceId, scope.repositoryId, scope.snapshotId, runId, runStatus]);

  async function open(citation: Citation) {
    if (!citation.filePath || !citation.fileSha256 || !citation.line) return;
    setSource({ citation, view: null, error: null });
    try {
      const view = await api.source(scope, citation.filePath, citation.fileSha256, citation.line, 8);
      setSource({ citation, view, error: null });
    } catch (e) {
      setSource({ citation, view: null, error: e instanceof Error ? e.message : String(e) });
    }
  }

  if (error) return <p className="live-error" role="alert">{error}</p>;
  if (!detail) return <p className="live-help">Loading the run record…</p>;
  const report = detail.report;
  if (!report) {
    return (
      <div className="findings-empty">
        <p>No report has been recorded for this run yet. Run status: <strong>{detail.summary.status}</strong> ({detail.summary.diagnostic}).</p>
        <p className="live-help">A report is written when the run finishes. This page refreshes when the run's status changes.</p>
      </div>
    );
  }
  if (!supportedReportVersion(report.version))
    return <p className="live-error">Unsupported report version {report.version}; nothing is shown rather than guessing.</p>;

  return (
    <div className="findings">
      <RunHeader report={report} />
      <HostCautions report={report} onOpen={open} />
      <section aria-labelledby="verdicts-heading">
        <h2 id="verdicts-heading">Hypotheses and verdicts</h2>
        <p className="live-help">The Investigator proposed these. The Reviewer weighed them against the Skeptic's objections, and the host checked the result. A verdict is a structured judgment by AI roles, not proof.</p>
        {verdicts(report).length === 0 && <p className="live-help">The Investigator recorded no hypotheses.</p>}
        {verdicts(report).map(({ hypothesis, reviewer, host }) => (
          <article className="finding-card" key={hypothesis.id}>
            <header>
              <span className="finding-id">{hypothesis.id}</span>
              <span className={'verdict verdict--' + (host?.recommendation ?? 'none')}>
                {host ? label(VERDICTS, host.recommendation) : 'No host-reconciled verdict'}
              </span>
              {host && !host.isVerifiedFact && <span className="finding-note">not a verified fact</span>}
            </header>
            <AiText label="Hypothesis (AI-written)" text={hypothesis.text} />
            <AiText label="Stated uncertainty" text={hypothesis.uncertainty} />
            <CitationList items={citations(report, 0, hypothesis.evidenceHandles)} onOpen={open} />
            {reviewer && <>
              <AiText label="Reviewer's reasoning (AI-written)" text={reviewer.explanation} />
              {reviewer.addressedConcernIds.length > 0 && <p className="finding-meta">Answers objections: {reviewer.addressedConcernIds.join(', ')}</p>}
            </>}
          </article>
        ))}
      </section>

      <section aria-labelledby="objections-heading">
        <h2 id="objections-heading">The Skeptic's objections</h2>
        {(report.skeptic.assessment?.concerns ?? []).length === 0 && <p className="live-help">No objections were recorded.</p>}
        {(report.skeptic.assessment?.concerns ?? []).map(concern => (
          <article className="finding-card finding-card--skeptic" key={concern.id}>
            <header><span className="finding-id">{concern.id}</span><span className="verdict">{label(CONCERN_KINDS, concern.kind)}</span></header>
            <AiText label="Objection (AI-written)" text={concern.explanation} />
            <CitationList items={citations(report, 1, concern.evidenceHandles)} onOpen={open} />
          </article>
        ))}
        {(report.reviewer.assessment?.unresolvedGlobalConcernIds ?? []).length > 0 &&
          <p className="finding-meta">Left unresolved by the Reviewer: {report.reviewer.assessment!.unresolvedGlobalConcernIds.join(', ')}</p>}
      </section>

      <section aria-labelledby="unknowns-heading">
        <h2 id="unknowns-heading">What the agents said they do not know</h2>
        <Unknowns title="Investigator" items={report.investigator.assessment?.unknowns ?? []} />
        <Unknowns title="Skeptic" items={report.skeptic.assessment?.unknowns ?? []} />
      </section>

      {source && <SourcePanel source={source} onClose={() => setSource(null)} />}
    </div>
  );
}

/**
 * A separate host-authored channel: not a fourth agent, not a reviewer
 * decision, and not an assertion about route reachability or runtime behavior.
 */
function HostCautions({ report, onOpen }: {
  report: Report; onOpen: (citation: Citation) => void;
}) {
  const view = hostCautionView(report);
  return (
    <section className="host-cautions-panel" aria-labelledby="host-cautions-heading">
      <div className="host-cautions-heading">
        <h2 id="host-cautions-heading">Code-analysis cautions (not AI)</h2>
        <span className="host-cautions-provenance">Host-written · saved source syntax</span>
      </div>
      <p className="live-help">
        These are deterministic, source-based cautions for the selected entry point,
        not AI findings, verified runtime behavior, or proof that an endpoint is reachable.
        They do not affect the Investigator, Skeptic, or Reviewer verdicts.
      </p>
      {view.state === 'legacy' &&
        <p className="host-cautions-message">
          This older report predates host-written cautions. No host inspection was recorded;
          this does not mean there are no issues.
        </p>}
      {view.state === 'not-evaluated' &&
        <p className="host-cautions-message">
          Host caution inspection was not completed for this report. The result is unknown,
          not a verified absence of cautions.
        </p>}
      {view.state === 'incompatible' &&
        <p className="live-error" role="status">
          Host caution data is missing, invalid, or uses an unsupported analyzer version.
          No host cautions are displayed; do not interpret this as zero cautions.
        </p>}
      {view.state === 'verified-empty' &&
        <p className="host-cautions-message">
          No host-written cautions were emitted by the current limited checks for this entry point.
          This does not establish runtime behavior, security, or absence of other issues.
        </p>}
      {view.state === 'verified-cautions' && <>
        <p className="host-cautions-count">
          {view.cautions.length} host-written {view.cautions.length === 1 ? 'caution' : 'cautions'} ·
          analyzer {view.analyzerVersion}
        </p>
        <ol className="host-cautions-list">
          {view.cautions.map(caution => (
            <li className="host-caution-card" key={caution.id}>
              <h3>{HOST_CAUTION_LABELS[caution.kind as keyof typeof HOST_CAUTION_LABELS]}</h3>
              <p className="host-caution-statement">{caution.statement}</p>
              <p className="finding-meta">Source-syntax observation only · {caution.status}</p>
              <HostCautionSources caution={caution} onOpen={onOpen} />
            </li>
          ))}
        </ol>
      </>}
    </section>
  );
}

function HostCautionSources({ caution, onOpen }: {
  caution: HostSyntaxCaution; onOpen: (citation: Citation) => void;
}) {
  return (
    <ul className="host-caution-anchors" aria-label="Saved snapshot source locations">
      {caution.anchors.map((anchor, index) => {
        const cite = hostSourceCitation(anchor);
        return (
          <li key={caution.id + ':' + index}>
            {cite
              ? <button type="button" className="citation"
                  aria-label={'Open verified saved source ' + anchor.filePath + ' at line ' + anchor.line}
                  onClick={() => onOpen(cite)}>{anchor.filePath}:{anchor.line}</button>
              : <span className="finding-meta">{anchor.filePath} · project-file candidate (no line available)</span>}
          </li>
        );
      })}
    </ul>
  );
}

function RunHeader({ report }: { report: Report }) {
  return (
    <section className="finding-summary" aria-label="Run summary">
      <p>Run <strong>{label(RUN_STATUS, report.progress.status)}</strong> · {new Date(report.finishedAtUtc).toLocaleString()}</p>
      <p className="finding-meta">
        Investigator: {label(ROLE_STATUS, report.progress.investigatorStatus)} · Skeptic: {label(ROLE_STATUS, report.progress.skepticStatus)} · Reviewer: {label(ROLE_STATUS, report.progress.reviewerStatus)}
      </p>
      <p className="finding-meta">Models: {report.investigatorModelId} / {report.skepticModelId} / {report.reviewerModelId} · host diagnostic: {report.diagnostic}</p>
      {report.reviewer.reconciliation && !report.reviewer.reconciliation.check.accepted &&
        <p className="live-error">The host rejected the Reviewer's reconciliation ({report.reviewer.reconciliation.check.diagnostic}); no verdicts are shown as reconciled.</p>}
      <p className="finding-meta">"Accepted" means the host's contract checks passed, not that the claims are true. AI-written text is shown exactly as written.</p>
    </section>
  );
}

function AiText({ label: title, text }: { label: string; text: string }) {
  if (!text) return null;
  return <div className="ai-text"><span className="detail-label">{title}</span><p>{text}</p></div>;
}

function CitationList({ items, onOpen }: { items: Citation[]; onOpen: (c: Citation) => void }) {
  if (items.length === 0) return <p className="finding-meta">No evidence cited.</p>;
  return (
    <ul className="citation-list" aria-label="Cited evidence">
      {items.map(c => (
        <li key={c.handle}>
          {c.filePath && c.line
            ? <button type="button" className="citation" onClick={() => onOpen(c)}>{c.filePath}:{c.line}</button>
            : <span className="citation citation--none">{c.missing ? `${c.handle} (not in report)` : `${c.handle} (no source location)`}</span>}
          <span className="finding-meta">{c.kind.replace(/[ ()]+/g, '-').replace(/-+$/, '') === c.status ? c.status : `${c.kind} · ${c.status}`}</span>
        </li>
      ))}
    </ul>
  );
}

function Unknowns({ title, items }: { title: string; items: string[] }) {
  if (items.length === 0) return <p className="finding-meta">{title}: none recorded.</p>;
  return (
    <div className="ai-text">
      <span className="detail-label">{title} (AI-written)</span>
      <ul>{items.map((item, i) => <li key={i}>{item}</li>)}</ul>
    </div>
  );
}

function SourcePanel({ source, onClose }: {
  source: { citation: Citation; view: SourceView | null; error: string | null }; onClose: () => void;
}) {
  const { citation, view, error } = source;
  return (
    <section className="source-panel" aria-label={`Source ${citation.filePath}:${citation.line}`}>
      <header>
        <strong>{citation.kind === 'saved-source-syntax' ? 'Host caution · ' : ''}{citation.filePath}:{citation.line}</strong>
        <button type="button" className="secondary-action" onClick={onClose}>Close</button>
      </header>
      {error && <p className="live-error" role="alert">{error}</p>}
      {!error && !view && <p className="live-help">Loading saved source…</p>}
      {view && <>
        <pre className="source-lines">{view.lines.map(l => (
          <span key={l.number} className={l.number === view.focusLine ? 'source-line is-focus' : 'source-line'}>
            <span className="source-number">{String(l.number).padStart(4, ' ')}</span>{' '}{l.text}{l.truncated ? ' …' : ''}{'\n'}
          </span>
        ))}</pre>
        <p className="finding-meta">Lines {view.startLine}–{view.endLine} of {view.totalLines} · {view.caveat} Shown as untrusted text.</p>
      </>}
    </section>
  );
}
