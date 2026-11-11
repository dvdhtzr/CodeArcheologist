/**
 * MC4 report model (gate-c5-investigation-report-v1, enums as numbers) and
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
export type Report = {
  version: string; runId: string;
  investigatorModelId: string; skepticModelId: string; reviewerModelId: string;
  progress: { status: number; investigatorStatus: number; skepticStatus: number; reviewerStatus: number };
  startedAtUtc: string; finishedAtUtc: string;
  investigator: RoleOutcome; skeptic: RoleOutcome; reviewer: ReviewerOutcome;
  evidenceLinks: EvidenceLink[]; diagnostic: string;
};
export type RunDetail = { summary: RunSummary; report: Report | null };

export const REPORT_VERSION = 'gate-c5-investigation-report-v1';
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
