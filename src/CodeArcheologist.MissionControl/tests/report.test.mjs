import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { join, dirname } from 'node:path';
import {
  citations, hostCautionView, hostSourceCitation, label, verdicts, VERDICTS,
  REPORT_VERSION, LEGACY_REPORT_VERSION, HOST_CAUTION_ANALYZER_VERSION,
  HOST_CAUTION_LABELS, supportedReportVersion,
} from '../src/live/report.ts';
import { ApiClient } from '../src/live/api.ts';

const root = dirname(dirname(fileURLToPath(import.meta.url)));
const link = (role, handle, line) => ({ role, handle, canonicalId: 'c' + handle, kind: 0, sourceKind: 's', evidenceStatus: 'observed-syntax',
  filePath: 'A.cs', fileSha256: 'f'.repeat(64), line, column: 1 });
const report = (accepted = true) => ({
  version: 'gate-c5-investigation-report-v2', runId: 'r',
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


const hostAnchor = (filePath = 'Controllers/TodoController.cs', line = 11) => ({
  filePath, fileSha256: 'a'.repeat(64),
  line, column: 3, spanStart: 100, spanLength: 12,
});
const projectAnchor = {
  filePath: 'ProjectOne/One.csproj', fileSha256: 'b'.repeat(64),
  line: null, column: null, spanStart: null, spanLength: null,
};
const hostWarning = (kind = 'conditional-invocation') => ({
  id: 'host-caution:' + kind + ':entry-1',
  kind, status: 'host-syntax-caution',
  statement: 'A direct call appears in a conditional branch, not on every path.',
  analyzerVersion: HOST_CAUTION_ANALYZER_VERSION,
  anchors: [hostAnchor(), projectAnchor],
});
const hostReport = (overrides = {}) => ({
  ...report(), hostCautionStatus: 'verified',
  hostCautionAnalyzerVersion: HOST_CAUTION_ANALYZER_VERSION,
  hostCautions: [hostWarning()], ...overrides,
});

test('verified host cautions remain a separate source-syntax channel, never an AI verdict', () => {
  const r = hostReport();
  const view = hostCautionView(r);
  assert.equal(view.state, 'verified-cautions');
  assert.equal(view.analyzerVersion, HOST_CAUTION_ANALYZER_VERSION);
  assert.deepEqual(view.cautions, r.hostCautions);
  assert.equal(HOST_CAUTION_LABELS['conditional-invocation'], 'Conditional invocation');
  assert.equal(Object.keys(HOST_CAUTION_LABELS).length, 5, 'only five approved caution kinds');
  assert.equal(verdicts(r).length, 2, 'host warnings do not become agent hypotheses');
  assert.deepEqual(citations(r, 0, ['E1']).map(x => x.line), [5],
    'host anchors never enter the role-local agent citation ledger');
});

test('host UI distinguishes verified zero, not evaluated, historical, and incompatible', () => {
  const verifiedEmpty = hostCautionView(hostReport({ hostCautions: [] }));
  assert.equal(verifiedEmpty.state, 'verified-empty');
  assert.deepEqual(verifiedEmpty.cautions, []);
  assert.equal(hostCautionView(hostReport({
    hostCautionStatus: 'not-evaluated', hostCautions: [hostWarning()],
  })).state, 'not-evaluated', 'unverified warnings must not display');
  assert.equal(hostCautionView({
    ...hostReport(), version: LEGACY_REPORT_VERSION,
  }).state, 'legacy', 'v1 cannot acquire retroactive cautions');
  assert.equal(hostCautionView({ ...hostReport(), version: 'invented' }).state, 'incompatible');
  assert.equal(hostCautionView(hostReport({ hostCautions: undefined })).state, 'incompatible');
  assert.equal(hostCautionView(hostReport({ hostCautionAnalyzerVersion: 'unknown-v2' })).state, 'incompatible');
  assert.equal(hostCautionView(hostReport({ hostCautionStatus: 'unexpected' })).state, 'incompatible');
});

test('invalid host provenance fails closed before display or citation opening', () => {
  const mutations = [
    { hostCautions: [hostWarning('not-approved')] },
    { hostCautions: [{ ...hostWarning(), status: 'AI-finding' }] },
    { hostCautions: [{ ...hostWarning(), id: 'not-host-owned' }] },
    { hostCautions: [{ ...hostWarning(), statement: '' }] },
    { hostCautions: [hostWarning(), hostWarning()] },
    { hostCautions: [{ ...hostWarning(), anchors: [] }] },
    { hostCautions: [{ ...hostWarning(), anchors: [projectAnchor] }] },
    { hostCautions: [{ ...hostWarning(), anchors: [{ ...hostAnchor(), fileSha256: 'wrong' }] }] },
    { hostCautions: [{ ...hostWarning(), anchors: [{ ...hostAnchor(), line: 0 }] }] },
    { hostCautions: [{ ...hostWarning(), anchors: [{ ...hostAnchor(), line: null }] }] },
    { hostCautions: [{ ...hostWarning(), anchors: [{ ...hostAnchor(), spanLength: 0 }] }] },
    { hostCautions: Array.from({ length: 6 }, (_, i) => ({
        ...hostWarning(), id: 'host-caution:conditional-invocation:entry-' + i,
      })) },
  ];
  for (const change of mutations)
    assert.equal(hostCautionView(hostReport(change)).state, 'incompatible',
      'invalid source or caution provenance was rendered: ' + JSON.stringify(change));
});

test('host source links preserve exact saved path/hash/line, without inventing project-file lines', () => {
  const linked = hostSourceCitation(hostAnchor('Directory/My API.cs', 91));
  assert.deepEqual(linked, {
    handle: 'host-source-anchor',
    filePath: 'Directory/My API.cs',
    fileSha256: 'a'.repeat(64),
    line: 91,
    status: 'host-syntax-caution', kind: 'saved-source-syntax', missing: false,
  });
  assert.equal(hostSourceCitation(projectAnchor), null, 'project-level anchor is text only');
  assert.equal(hostSourceCitation({ ...hostAnchor(), line: null }), null);
  assert.equal(hostSourceCitation({ ...hostAnchor(), fileSha256: '../not-a-hash' }), null);
});

test('Findings shows host cautions separately and reuses the bounded source viewer safely', () => {
  const findings = readFileSync(join(root, 'src/live/Findings.tsx'), 'utf8');
  const css = readFileSync(join(root, 'src/style.css'), 'utf8');
  assert.ok(findings.includes('Code-analysis cautions (not AI)'));
  assert.ok(findings.includes('<HostCautions report={report} onOpen={open} />'));
  assert.ok(findings.indexOf('<HostCautions report={report} onOpen={open} />') <
    findings.indexOf('<section aria-labelledby="verdicts-heading">'));
  assert.ok(findings.includes('hostCautionView(report)'));
  assert.ok(findings.includes('hostSourceCitation(anchor)'));
  assert.ok(findings.includes('onClick={() => onOpen(cite)}'));
  assert.ok(findings.includes('api.source(scope, citation.filePath, citation.fileSha256, citation.line, 8)'));
  assert.ok(findings.includes('{caution.statement}'));
  assert.ok(findings.includes('No host-written cautions were emitted'));
  assert.ok(findings.includes('not a verified absence'));
  assert.ok(findings.includes('predates host-written cautions'));
  assert.ok(findings.includes("view.state === 'incompatible'"));
  assert.ok(findings.includes('project-file candidate (no line available)'));
  assert.ok(css.includes('.host-cautions-panel'));
  assert.ok(!/dangerouslySetInnerHTML|innerHTML|markdown\s*\(|marked\s*\(/i.test(findings));
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
