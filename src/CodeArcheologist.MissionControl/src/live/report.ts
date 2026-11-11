/**
 * MC4 report model (gate-c5-investigation-report-v2, enums as numbers) and
 * pure helpers. Model-authored text (hypotheses, concerns, explanations,
 * unknowns) is UNTRUSTED: render it as plain text only. A host-accepted
 * assessment passed contract checks; it is not proof that its claims are true.
 */
import type { RunSummary } from './api.ts';

export type EvidenceLink = {
  role: number; handle: string; canonicalId: string; kind: number; sourceKind: string; evidenceStatus: string;
  filePath: string | null; fileSha256: string | null; line: number | null; column: number | null;
};
export type Hypothesis = { id: string; text: string; evidenceHandles: string[]; uncertainty: string };
export type Concern = { id: string; kind: number; explanation: string; evidenceHandles: string[] };
export type Assessment = { hypotheses: Hypothesis[]; concerns: Concern[]; unknowns: string[] };
export type RoleOutcome = { status: number; diagnostic: string; assessment: Assessment | null };
export type ReviewDecision = { hypothesisId: string; recommendation: number; addressedConcernIds: string[]; explanation: string };
export type ReviewerOutcome = {
  status: number; diagnostic: string;
  assessment: { decisions: ReviewDecision[]; unresolvedGlobalConcernIds: string[] } | null;
  reconciliation: {
    check: { accepted: boolean; diagnostic: string };
    dispositions: { hypothesisId: string; recommendation: number; isVerifiedFact: boolean }[];
  } | null;
};
export type HostCautionAnchor = {
  filePath: string; fileSha256: string;
  line: number | null; column: number | null;
  spanStart: number | null; spanLength: number | null;
};
export type HostSyntaxCaution = {
  id: string; kind: string; status: 'host-syntax-caution';
  statement: string; analyzerVersion: string; anchors: HostCautionAnchor[];
};
export type Report = {
  version: string; runId: string;
  investigatorModelId: string; skepticModelId: string; reviewerModelId: string;
  progress: { status: number; investigatorStatus: number; skepticStatus: number; reviewerStatus: number };
  startedAtUtc: string; finishedAtUtc: string;
  investigator: RoleOutcome; skeptic: RoleOutcome; reviewer: ReviewerOutcome;
  evidenceLinks: EvidenceLink[]; diagnostic: string;
  /** Absent only on legacy v1 stored reports. This field is host-authored, not AI evidence. */
  hostCautionStatus?: 'verified' | 'not-evaluated';
  hostCautionAnalyzerVersion?: string;
  hostCautions?: HostSyntaxCaution[];
};
export type RunDetail = { summary: RunSummary; report: Report | null };

export const REPORT_VERSION = 'gate-c5-investigation-report-v2';
export const LEGACY_REPORT_VERSION = 'gate-c5-investigation-report-v1';
export function supportedReportVersion(version: string): boolean {
  return version === REPORT_VERSION || version === LEGACY_REPORT_VERSION;
}
/**
 * Host cautions are determined by fixed Roslyn source-syntax checks and were
 * NOT authored, approved or verified as runtime facts by any AI role.
 * Fail closed for missing/unknown/incompatible warning data.
 */
export const HOST_CAUTION_ANALYZER_VERSION = 'roslyn-host-cautions-v1';
const CAUTION_KINDS = [
  'unobserved-registration',
  'route-group-receiver',
  'sibling-modifier-difference',
  'conditional-invocation',
  'multiple-project-candidates',
] as const;
export const HOST_CAUTION_LABELS: Readonly<Record<(typeof CAUTION_KINDS)[number], string>> = {
  'unobserved-registration': 'Registration call not observed',
  'route-group-receiver': 'Route group receiver',
  'sibling-modifier-difference': 'Different authorization modifiers',
  'conditional-invocation': 'Conditional invocation',
  'multiple-project-candidates': 'Multiple project candidates',
};

export type HostCautionView =
  | { state: 'legacy' | 'not-evaluated' | 'incompatible'; cautions: readonly []; analyzerVersion: null }
  | { state: 'verified-empty' | 'verified-cautions'; cautions: readonly HostSyntaxCaution[]; analyzerVersion: string };

function validHostAnchor(anchor: HostCautionAnchor): boolean {
  if (!anchor || typeof anchor.filePath !== 'string' || !anchor.filePath.trim() ||
      typeof anchor.fileSha256 !== 'string' || !/^[0-9a-f]{64}$/i.test(anchor.fileSha256))
    return false;
  // The static project inspector supplies file-level anchors with null spans.
  // Never fabricate line 1 for a file-level anchor.
  if (anchor.line == null || anchor.column == null ||
      anchor.spanStart == null || anchor.spanLength == null)
    return anchor.line === null && anchor.column === null &&
      anchor.spanStart === null && anchor.spanLength === null;
  return Number.isSafeInteger(anchor.line) && anchor.line >= 1 &&
    Number.isSafeInteger(anchor.column) && anchor.column >= 1 &&
    Number.isSafeInteger(anchor.spanStart) && anchor.spanStart >= 0 &&
    Number.isSafeInteger(anchor.spanLength) && anchor.spanLength >= 1;
}

export function hostCautionView(report: Report): HostCautionView {
  const absent = (state: 'legacy' | 'not-evaluated' | 'incompatible'): HostCautionView =>
    ({ state, cautions: [], analyzerVersion: null });

  if (report.version === LEGACY_REPORT_VERSION) return absent('legacy');
  if (report.version !== REPORT_VERSION) return absent('incompatible');
  if (report.hostCautionStatus === 'not-evaluated') return absent('not-evaluated');
  if (report.hostCautionStatus !== 'verified' ||
      report.hostCautionAnalyzerVersion !== HOST_CAUTION_ANALYZER_VERSION ||
      !Array.isArray(report.hostCautions) || report.hostCautions.length > 5)
    return absent('incompatible');

  const kinds = new Set<string>();
  const ids = new Set<string>();
  for (const caution of report.hostCautions) {
    if (!caution || typeof caution.kind !== 'string' ||
        !CAUTION_KINDS.includes(caution.kind as (typeof CAUTION_KINDS)[number]) ||
        typeof caution.id !== 'string' || !caution.id.startsWith('host-caution:' + caution.kind + ':'))
      return absent('incompatible');
    if (kinds.has(caution.kind) || ids.has(caution.id) ||
        caution.status !== 'host-syntax-caution' ||
        caution.analyzerVersion !== HOST_CAUTION_ANALYZER_VERSION ||
        typeof caution.statement !== 'string' ||
        !caution.statement.trim() || caution.statement.length > 1000 ||
        !Array.isArray(caution.anchors) || caution.anchors.length < 1 || caution.anchors.length > 6 ||
        !caution.anchors.every(validHostAnchor) ||
        !caution.anchors.some(anchor => anchor.line !== null))
      return absent('incompatible');
    kinds.add(caution.kind);
    ids.add(caution.id);
  }
  return { state: report.hostCautions.length ? 'verified-cautions' : 'verified-empty',
    cautions: report.hostCautions, analyzerVersion: HOST_CAUTION_ANALYZER_VERSION };
}

/** Source opener intentionally returns nothing for file-level project anchors. */
export function hostSourceCitation(anchor: HostCautionAnchor): Citation | null {
  if (!validHostAnchor(anchor) || anchor.line === null) return null;
  return {
    handle: 'host-source-anchor',
    filePath: anchor.filePath,
    fileSha256: anchor.fileSha256,
    line: anchor.line,
    status: 'host-syntax-caution',
    kind: 'saved-source-syntax',
    missing: false,
  };
}

export const ROLE_STATUS = ['Not started', 'Running', 'Accepted by host checks', 'Rejected by host checks', 'Failed', 'Timed out', 'Cancelled'];
export const VERDICTS = ['Structurally supported', 'Plausible hypothesis', 'Contested or insufficient', 'Rejected'];
export const CONCERN_KINDS = ['Structural contradiction', 'Unsupported inference', 'Insufficient evidence', 'Competing hypothesis', 'Coverage limitation'];
export const RUN_STATUS = ['Created', 'Evidence verified', 'Independent assessment', 'Reconciliation', 'Completed', 'Incomplete', 'Cancelled', 'Rejected'];
const EVIDENCE_KINDS = ['observed syntax', 'candidate', 'provisional (unverified)'];

export function label(list: readonly string[], value: number): string {
  return list[value] ?? `Unknown (${value})`;
}

export type Citation = {
  handle: string; filePath: string | null; fileSha256: string | null; line: number | null;
  status: string; kind: string; missing: boolean;
};

/** Resolves a role's cited handles through that role's own evidence links only. */
export function citations(report: Report, role: 0 | 1, handles: readonly string[]): Citation[] {
  return handles.filter(h => h.length > 0).map(handle => {
    const link = report.evidenceLinks.find(l => l.role === role && l.handle === handle);
    return link
      ? { handle, filePath: link.filePath, fileSha256: link.fileSha256, line: link.line,
          status: link.evidenceStatus, kind: EVIDENCE_KINDS[link.kind] ?? link.sourceKind, missing: false }
      : { handle, filePath: null, fileSha256: null, line: null, status: 'not-in-report', kind: 'unknown', missing: true };
  });
}

export type Verdict = {
  hypothesis: Hypothesis; reviewer: ReviewDecision | null; host: { recommendation: number; isVerifiedFact: boolean } | null;
};

/**
 * Pairs each Investigator hypothesis with the Reviewer's decision and the
 * host's reconciled disposition. The host disposition is authoritative.
 */
export function verdicts(report: Report): Verdict[] {
  const hypotheses = report.investigator.assessment?.hypotheses ?? [];
  return hypotheses.map(hypothesis => ({
    hypothesis,
    reviewer: report.reviewer.assessment?.decisions.find(d => d.hypothesisId === hypothesis.id) ?? null,
    host: report.reviewer.reconciliation?.check.accepted
      ? report.reviewer.reconciliation.dispositions.find(d => d.hypothesisId === hypothesis.id) ?? null
      : null,
  }));
}
