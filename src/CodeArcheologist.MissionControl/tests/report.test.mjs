import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { join, dirname } from 'node:path';
import { citations, label, verdicts, VERDICTS, REPORT_VERSION, LEGACY_REPORT_VERSION, supportedReportVersion } from '../src/live/report.ts';
import { ApiClient } from '../src/live/api.ts';

const root = dirname(dirname(fileURLToPath(import.meta.url)));
const link = (role, handle, line) => ({ role, handle, canonicalId: 'c' + handle, kind: 0, sourceKind: 's', evidenceStatus: 'observed-syntax',
  filePath: 'A.cs', fileSha256: 'f'.repeat(64), line, column: 1 });
const report = (accepted = true) => ({
  version: 'gate-c5-investigation-report-v1', runId: 'r',
  investigator: { status: 2, diagnostic: '', assessment: { hypotheses: [
    { id: 'H1', text: 'Get calls Help', evidenceHandles: ['E1', ''], uncertainty: 'u' },
    { id: 'H2', text: 'Second', evidenceHandles: ['E9'], uncertainty: '' }], concerns: [], unknowns: [] } },
  skeptic: { status: 2, diagnostic: '', assessment: { hypotheses: [], concerns: [{ id: 'C1', kind: 2, explanation: 'x', evidenceHandles: ['E1'] }], unknowns: [] } },
  reviewer: { status: 2, diagnostic: '',
    assessment: { decisions: [{ hypothesisId: 'H1', recommendation: 0, addressedConcernIds: ['C1'], explanation: 'e' }], unresolvedGlobalConcernIds: [] },
    reconciliation: { check: { accepted, diagnostic: 'd' }, dispositions: [{ hypothesisId: 'H1', recommendation: 1, isVerifiedFact: false }] } },
  evidenceLinks: [link(0, 'E1', 5), link(1, 'E1', 9)],
});

test('v2 reports are supported and legacy v1 history remains readable', () => {
  assert.equal(REPORT_VERSION, 'gate-c5-investigation-report-v2');
  assert.equal(LEGACY_REPORT_VERSION, 'gate-c5-investigation-report-v1');
  assert.ok(supportedReportVersion(REPORT_VERSION));
  assert.ok(supportedReportVersion(LEGACY_REPORT_VERSION));
  assert.equal(supportedReportVersion('unknown-contract'), false);
  assert.equal(report().version, REPORT_VERSION);
});

test('citations resolve only through the citing role’s own links', () => {
  const r = report();
  assert.deepEqual(citations(r, 0, ['E1', '']).map(c => c.line), [5], 'empty handles are dropped');
  assert.equal(citations(r, 1, ['E1'])[0].line, 9, 'same handle name, other role, other location');
  const missing = citations(r, 0, ['E9'])[0];
  assert.equal(missing.missing, true);
  assert.equal(missing.filePath, null, 'never invents a location');
});

test('verdicts use the host-reconciled disposition, and none when the host rejected reconciliation', () => {
  const [h1, h2] = verdicts(report());
  assert.equal(h1.host.recommendation, 1, 'host disposition wins over the reviewer’s own (0)');
  assert.equal(h1.reviewer.recommendation, 0);
  assert.equal(h2.host, null);
  assert.ok(verdicts(report(false)).every(v => v.host === null));
  assert.equal(label(VERDICTS, 1), 'Plausible hypothesis');
  assert.equal(label(VERDICTS, 9), 'Unknown (9)');
});

test('source requests carry the exact path, hash, line and bounded context', async () => {
  const calls = [];
  const impl = async (url, init) => { calls.push(url); return new Response(JSON.stringify(calls.length === 1 ? { accessToken: 'e'.repeat(64) } : {}), { status: 200 }); };
  const api = new ApiClient('http://127.0.0.1:5127', impl);
  await api.pair('x');
  const id = '11111111-1111-4111-8111-111111111111';
  await api.source({ workspaceId: id, repositoryId: id, snapshotId: id }, 'Sub/A b.cs', 'f'.repeat(64), 12, 8);
  const url = new URL(calls[1]);
  assert.ok(url.pathname.endsWith('/source'));
  assert.equal(url.searchParams.get('path'), 'Sub/A b.cs');
  assert.equal(url.searchParams.get('line'), '12');
  assert.equal(url.searchParams.get('context'), '8');
});

test('AI-written text is never rendered as HTML or Markdown', () => {
  const findings = readFileSync(join(root, 'src/live/Findings.tsx'), 'utf8');
  const code = findings.replace(/\/\*[\s\S]*?\*\//g, '').replace(/\/\/.*$/gm, '');
  assert.ok(!/dangerouslySetInnerHTML|innerHTML|marked|markdown/i.test(code));
  assert.ok(findings.includes('AI-written'));
  assert.ok(findings.includes('not that the claims are true'));
  assert.ok(findings.includes('not a verified fact'));
});
