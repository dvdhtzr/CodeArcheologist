import test from 'node:test';
import assert from 'node:assert/strict';
import { launchMatchesSelection, sameSnapshot, verifiedQuestionAdmission } from '../src/live/questionJourney.ts';

const workspaceId = '11111111-1111-4111-8111-111111111111';
const repositoryId = '22222222-2222-4222-8222-222222222222';
const snapshotId = '33333333-3333-4333-8333-333333333333';
const runId = '44444444-4444-4444-8444-444444444444';
const questionId = '55555555-5555-4555-8555-555555555555';
const scope = { workspaceId, repositoryId, snapshotId };
const fingerprint = 'A'.repeat(64);
const nodeId = 'http-candidate:source-anchored';
const savedScope = {
  ...scope, fingerprintSha256: fingerprint, entryPointId: nodeId,
  evidenceContractVersion: 'snapshot-agent-evidence-tools-v2',
};
const result = {
  investigation: {
    runId, scope: savedScope, status: 'running', diagnostic: 'queued',
    startedAtUtc: '2026-11-11T00:00:00Z', finishedAtUtc: null,
  },
  questionContext: {
    contractVersion: 'r3-investigation-context-v1',
    questionId, runId, scope: savedScope, subjectNodeId: nodeId,
    subjectNodeKind: 'http-endpoint-attribute',
    questionKind: 'explain-http-entry-point',
    questionTemplateVersion: 'r3-explain-http-entry-point-v1',
    capabilityPolicyVersion: 'gate-c-evidence-capabilities-v6',
    budgetProfileVersion: 'r3-legacy-gate-c6b-budget-v1',
    createdAtUtc: '2026-11-11T00:00:00Z',
    contextDigest: 'B'.repeat(64),
  },
};

test('R3 admission must bind question, run, subject and snapshot exactly', () => {
  assert.deepEqual(verifiedQuestionAdmission(result, scope, nodeId, fingerprint),
    result.questionContext);
  assert.deepEqual(verifiedQuestionAdmission({
    ...result, questionContext: { ...result.questionContext, subjectNodeKind: 'http-minimal-mapping' },
  }, scope, nodeId, fingerprint).subjectNodeKind, 'http-minimal-mapping');
  const bad = [
    { ...result, questionContext: { ...result.questionContext, questionId: runId } },
    { ...result, questionContext: { ...result.questionContext, runId: questionId } },
    { ...result, questionContext: { ...result.questionContext, questionKind: 'freeform-shell' } },
    { ...result, questionContext: { ...result.questionContext, subjectNodeId: 'another-node' } },
    { ...result, questionContext: { ...result.questionContext, subjectNodeKind: 'syntax-declaration' } },
    { ...result, questionContext: { ...result.questionContext, scope: { ...savedScope, snapshotId: questionId } } },
    { ...result, questionContext: { ...result.questionContext, scope: { ...savedScope, entryPointId: 'other' } } },
    { ...result, investigation: { ...result.investigation, scope: { ...savedScope, fingerprintSha256: 'F'.repeat(64) } } },
    { ...result, questionContext: { ...result.questionContext, capabilityPolicyVersion: 'new-unapproved' } },
    { ...result, questionContext: { ...result.questionContext, contextDigest: '' } },
    { ...result, questionContext: { ...result.questionContext, createdAtUtc: 'not-a-date' } },
  ];
  for (const value of bad)
    assert.throws(() => verifiedQuestionAdmission(value, scope, nodeId, fingerprint), /did not match/);
  assert.throws(() => verifiedQuestionAdmission(result, { ...scope, snapshotId: questionId },
    nodeId, fingerprint), /did not match/);
});

test('R3 adaptive admissions require the explicit v2 profile without weakening stable v1', () => {
  const adaptive = {
    ...result,
    questionContext: {
      ...result.questionContext,
      contractVersion: 'r3-adaptive-question-context-v2',
      questionTemplateVersion: 'r3-adaptive-explain-http-candidate-v1',
      capabilityPolicyVersion: 'r3-adaptive-capabilities-v1',
      budgetProfileVersion: 'r3-adaptive-six-call-one-followup-v1',
    },
  };
  assert.deepEqual(verifiedQuestionAdmission(adaptive, scope, nodeId, fingerprint,
    'adaptive'), adaptive.questionContext);
  assert.throws(() => verifiedQuestionAdmission(adaptive, scope, nodeId, fingerprint),
    /did not match/);
  assert.throws(() => verifiedQuestionAdmission(result, scope, nodeId, fingerprint,
    'adaptive'), /did not match/);
  for (const invalid of [
    { ...adaptive.questionContext, capabilityPolicyVersion: 'gate-c-evidence-capabilities-v6' },
    { ...adaptive.questionContext, questionTemplateVersion: 'r3-explain-http-entry-point-v1' },
    { ...adaptive.questionContext, budgetProfileVersion: 'r3-legacy-gate-c6b-budget-v1' },
    { ...adaptive.questionContext, scope: { ...savedScope, snapshotId: questionId } },
    { ...adaptive.questionContext, subjectNodeId: 'another-node' },
    { ...adaptive.questionContext, questionId: runId },
    { ...adaptive.questionContext, contextDigest: '' },
  ]) {
    assert.throws(() => verifiedQuestionAdmission({
      ...adaptive, questionContext: invalid,
    }, scope, nodeId, fingerprint, 'adaptive'), /did not match/);
  }
});

test('R3 launched question remains navigable only in original API session and snapshot', () => {
  const api = {};
  const launch = { api, scope, subjectNodeId: nodeId, questionContext: result.questionContext,
    run: result.investigation, subjectLabel: 'Get', fingerprintSha256: fingerprint };
  assert.equal(launchMatchesSelection(launch, api, scope), true);
  assert.equal(launchMatchesSelection(launch, {}, scope), false);
  assert.equal(launchMatchesSelection(launch, api, { ...scope, snapshotId: questionId }), false);
  assert.equal(launchMatchesSelection(launch, api, null), false);
  assert.equal(launchMatchesSelection(null, api, scope), false);
  assert.equal(sameSnapshot(scope, { ...scope }), true);
  assert.equal(sameSnapshot(scope, { ...scope, repositoryId: questionId }), false);
});
