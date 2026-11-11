import type { ApiClient, QuestionAdmission, QuestionContext, RunSummary, Scope } from './api.ts';

export const R3_QUESTION_KIND = 'explain-http-entry-point';
export const R3_CONTEXT_VERSION = 'r3-investigation-context-v1';

export type AtlasQuestionLaunch = {
  api: ApiClient;
  scope: Scope;
  fingerprintSha256: string;
  subjectNodeId: string;
  subjectLabel: string;
  questionContext: QuestionContext;
  run: RunSummary;
};

export function sameSnapshot(a: Scope, b: Scope): boolean {
  return a.workspaceId === b.workspaceId &&
    a.repositoryId === b.repositoryId &&
    a.snapshotId === b.snapshotId;
}

/** The host is the authority. The UI independently rejects mismatched or
 * incomplete admission responses before navigating, but does not pretend
 * that a matching digest makes model-authored claims semantically verified. */
export function verifiedQuestionAdmission(data: QuestionAdmission,
  scope: Scope, subjectNodeId: string, fingerprintSha256: string): QuestionContext {
  const run = data?.investigation;
  const context = data?.questionContext;
  const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
  if (!run || !context || context.contractVersion !== R3_CONTEXT_VERSION ||
      !guid.test(run.runId) || !guid.test(context.questionId) ||
      run.runId !== context.runId || run.runId === context.questionId ||
      !sameSnapshot(run.scope, scope) || !sameSnapshot(context.scope, scope) ||
      run.scope.fingerprintSha256 !== fingerprintSha256 ||
      context.scope.fingerprintSha256 !== fingerprintSha256 ||
      context.scope.evidenceContractVersion !== 'snapshot-agent-evidence-tools-v2' ||
      run.scope.evidenceContractVersion !== context.scope.evidenceContractVersion ||
      run.scope.entryPointId !== subjectNodeId ||
      context.scope.entryPointId !== subjectNodeId ||
      context.subjectNodeId !== subjectNodeId ||
      (context.subjectNodeKind !== 'http-endpoint-attribute' &&
       context.subjectNodeKind !== 'http-minimal-mapping') ||
      context.questionKind !== R3_QUESTION_KIND ||
      context.questionTemplateVersion !== 'r3-explain-http-entry-point-v1' ||
      context.capabilityPolicyVersion !== 'gate-c-evidence-capabilities-v6' ||
      context.budgetProfileVersion !== 'r3-legacy-gate-c6b-budget-v1' ||
      !/^[A-F0-9]{64}$/.test(context.contextDigest) ||
      !Number.isFinite(Date.parse(context.createdAtUtc)))
    throw new Error('The question admission did not match the selected saved node and snapshot.');

  return context;
}

export function launchMatchesSelection(launch: AtlasQuestionLaunch | null,
  api: ApiClient | null, scope: Scope | null): launch is AtlasQuestionLaunch {
  return !!launch && !!api && !!scope &&
    launch.api === api && sameSnapshot(launch.scope, scope);
}
