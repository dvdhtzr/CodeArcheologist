import { test, expect } from '@playwright/test';
import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { startApi, stopApi, bootstrap } from '../../../../tests/api/harness.mjs';

// R3 PR2 browser journey: actual API pairing, saved capture, graph, and
// navigation. ONLY question admission / fake run GET are intercepted so that
// the test does not launch Ollama or publish fabricated AI assessments.
const origin = 'http://127.0.0.1:5175';
let directory, source, api;
const code = `namespace Journey;
[Route("api/journey")]
public class JourneyController
{
    [HttpGet]
    public int Get() => Helper();
    private int Helper() => 23;
}
`;

test.beforeAll(async () => {
  directory = await mkdtemp(join(tmpdir(), 'code-archeologist-r3-browser-'));
  source = join(directory, 'source');
  await mkdir(source);
  await writeFile(join(source, 'JourneyController.cs'), code);
  api = await startApi(join(directory, 'data'), origin);
});
test.afterAll(async () => {
  await stopApi(api);
  if (directory) await rm(directory, { recursive: true, force: true });
});

test('R3 PR2: explicit Atlas question survives Observatory and Findings without changing graph scope', async ({ page }) => {
  test.setTimeout(120_000);
  await page.goto('/');
  await page.getByLabel('Pairing code (shown in the API window)').fill(bootstrap);
  await page.getByRole('button', { name: 'Connect', exact: true }).click();
  await expect(page.getByText('API connected')).toBeVisible();
  await page.getByPlaceholder('New workspace name').fill('R3 Browser Question');
  await page.getByRole('button', { name: 'Create', exact: true }).click();
  await expect(page.getByRole('combobox', { name: /Workspace/ })).not.toHaveValue('');
  await page.getByPlaceholder('Local folder path to register').fill(source);
  await page.getByRole('button', { name: 'Register', exact: true }).click();
  await expect(page.getByRole('combobox', { name: /Repository/ })).not.toHaveValue('');
  await page.getByRole('button', { name: 'Capture new snapshot' }).click();
  await expect(page.getByRole('combobox', { name: /Snapshot/ })).not.toHaveValue('');
  const scope = {
    workspaceId: await page.getByRole('combobox', { name: /Workspace/ }).inputValue(),
    repositoryId: await page.getByRole('combobox', { name: /Repository/ }).inputValue(),
    snapshotId: await page.getByRole('combobox', { name: /Snapshot/ }).inputValue(),
  };
  await page.getByRole('button', { name: 'Architecture Explorer' }).click();
  await page.getByRole('button', { name: 'Load Architecture Atlas' }).click();
  await expect(page.locator('.atlas-provenance code')).toBeVisible();
  const fingerprintSha256 = await page.locator('.atlas-provenance code').innerText();
  await page.getByRole('combobox', { name: 'Atlas node kind' }).selectOption('http-endpoint-attribute');
  await page.locator('.atlas-search-results button').first().click();
  await expect(page.locator('.atlas-details')).toContainText('http-endpoint-attribute');
  const candidateLabel = await page.locator('.atlas-details .atlas-selected-label').innerText();
  await expect(page.getByRole('button', { name: 'Investigate this HTTP candidate' })).toBeVisible();
  await page.waitForTimeout(200);
  const before = await page.locator('.atlas-viewport .react-flow__viewport')
    .evaluate(el => {
      const matrix = new DOMMatrixReadOnly(getComputedStyle(el).transform);
      return { x: matrix.m41, y: matrix.m42, zoom: matrix.a };
    });

  const runId = randomUUID(), questionId = randomUUID();
  let fake, admissions = 0;
  const mockHeaders = { 'Access-Control-Allow-Origin': origin, Vary: 'Origin' };
  await page.route('**/investigations/questions', async route => {
    if (route.request().method() !== 'POST') return route.continue();
    admissions++;
    const body = route.request().postDataJSON();
    expect(body.questionKind).toBe('explain-http-entry-point');
    const savedScope = { ...scope, fingerprintSha256, entryPointId: body.subjectNodeId,
      evidenceContractVersion: 'snapshot-agent-evidence-tools-v2' };
    fake = {
      investigation: { runId, scope: savedScope, reportVersion: 'gate-c5-investigation-report-v1',
        status: 'running', diagnostic: 'queued', startedAtUtc: new Date().toISOString(), finishedAtUtc: null },
      questionContext: { contractVersion: 'r3-investigation-context-v1', questionId, runId,
        scope: savedScope, subjectNodeId: body.subjectNodeId,
        subjectNodeKind: 'http-endpoint-attribute', questionKind: 'explain-http-entry-point',
        questionTemplateVersion: 'r3-explain-http-entry-point-v1',
        capabilityPolicyVersion: 'gate-c-evidence-capabilities-v6',
        budgetProfileVersion: 'r3-legacy-gate-c6b-budget-v1',
        createdAtUtc: new Date().toISOString(), contextDigest: 'A'.repeat(64) },
    };
    await route.fulfill({ status: 202, contentType: 'application/json',
      headers: mockHeaders, body: JSON.stringify(fake) });
  });
  await page.route('**/investigations/' + runId + '/events', route =>
    route.fulfill({ status: 200, contentType: 'application/json',
      headers: mockHeaders, body: '[]' }));
  await page.route('**/investigations/' + runId, route =>
    route.fulfill({ status: 200, contentType: 'application/json',
      headers: mockHeaders, body: JSON.stringify({ summary: fake.investigation, report: null }) }));

  await page.getByRole('button', { name: 'Investigate this HTTP candidate' }).click();
  await expect(page.getByRole('heading', { name: 'Run an investigation' })).toBeVisible();
  await expect(page.getByLabel('Current Atlas investigation')).toContainText(candidateLabel);
  expect(admissions).toBe(1, 'a single explicit click issues one admission');
  await page.getByRole('button', { name: 'View question findings' }).click();
  await expect(page.getByText(/No report has been recorded for this run yet\./)).toBeVisible();
  await expect(page.getByText('Host-recorded question ID:')).toContainText(questionId);
  await page.getByRole('button', { name: 'Return to selected Atlas evidence' }).click();
  await expect(page.getByRole('heading', { name: 'Architecture Atlas' })).toBeVisible();
  await expect(page.locator('.atlas-details')).toContainText('http-endpoint-attribute');
  await expect(page.locator('.atlas-provenance code')).toHaveText(fingerprintSha256);
  await expect.poll(async () => {
    const next = await page.locator('.atlas-viewport .react-flow__viewport').evaluate(el => {
      const matrix = new DOMMatrixReadOnly(getComputedStyle(el).transform);
      return { x: matrix.m41, y: matrix.m42, zoom: matrix.a };
    });
    return Math.abs(next.x - before.x) < 2 &&
      Math.abs(next.y - before.y) < 2 &&
      Math.abs(next.zoom - before.zoom) < 0.02;
  }).toBe(true);
  expect(admissions).toBe(1, 'navigation must not start another model run');

  // R3 PR3c: a separate explicit action is required for adaptive v2.
  // Only this new admission is intercepted; actual API pairing, capture,
  // Atlas graph and node selection above remain real and verified. No Ollama
  // work is dispatched by this browser-only acceptance.
  const adaptiveRunId = randomUUID(), adaptiveQuestionId = randomUUID();
  let adaptiveAdmissions = 0;
  let adaptiveFake;
  await page.route('**/investigations/questions/adaptive', async route => {
    if (route.request().method() !== 'POST') return route.continue();
    adaptiveAdmissions++;
    const body = route.request().postDataJSON();
    expect(body.questionKind).toBe('explain-http-entry-point');
    expect(body.subjectNodeId).toBe(fake.questionContext.subjectNodeId);
    const savedScope = { ...scope, fingerprintSha256,
      entryPointId: body.subjectNodeId,
      evidenceContractVersion: 'snapshot-agent-evidence-tools-v2' };
    adaptiveFake = {
      investigation: {
        ...fake.investigation, runId: adaptiveRunId, scope: savedScope,
      },
      questionContext: {
        ...fake.questionContext,
        contractVersion: 'r3-adaptive-question-context-v2',
        questionId: adaptiveQuestionId, runId: adaptiveRunId,
        scope: savedScope,
        questionTemplateVersion: 'r3-adaptive-explain-http-candidate-v1',
        capabilityPolicyVersion: 'r3-adaptive-capabilities-v1',
        budgetProfileVersion: 'r3-adaptive-six-call-one-followup-v1',
      },
    };
    await route.fulfill({ status: 202, contentType: 'application/json',
      headers: mockHeaders, body: JSON.stringify(adaptiveFake) });
  });
  await page.route('**/investigations/' + adaptiveRunId + '/events', route =>
    route.fulfill({ status: 200, contentType: 'application/json',
      headers: mockHeaders, body: '[]' }));
  await page.route('**/investigations/' + adaptiveRunId, route =>
    route.fulfill({ status: 200, contentType: 'application/json',
      headers: mockHeaders, body: JSON.stringify({
        summary: adaptiveFake.investigation, report: null,
      }) }));
  await expect(page.getByRole('button', {
    name: 'Investigate experimentally (adaptive)',
  })).toBeVisible();
  await page.getByRole('button', {
    name: 'Investigate experimentally (adaptive)',
  }).click();
  await expect(page.getByLabel('Current Atlas investigation'))
    .toContainText('Experimental adaptive investigation');
  await expect(page.getByLabel('Current Atlas investigation'))
    .toContainText('one host-governed follow-up per role');
  expect(adaptiveAdmissions).toBe(1);
  expect(admissions).toBe(1, 'adaptive mode must not submit a stable admission');
  await page.getByRole('button', { name: 'View question findings' }).click();
  await expect(page.getByText('Host-recorded question ID:'))
    .toContainText(adaptiveQuestionId);
  await page.getByRole('button', { name: 'Return to selected Atlas evidence' }).click();
  await expect(page.locator('.atlas-details')).toContainText('http-endpoint-attribute');
  expect(adaptiveAdmissions).toBe(1, 'adaptive navigation must never re-admit');

  // Changing to a non-HTTP method removes the launch action. A graph
  // relationship or a similar spelling must not silently authorize it.
  await page.getByRole('combobox', { name: 'Atlas node kind' }).selectOption('syntax-declaration');
  await page.getByPlaceholder('Name, file, or declaration').fill('Helper');
  await page.locator('.atlas-search-results button').first().click();
  await expect(page.getByRole('button', { name: 'Investigate this HTTP candidate' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Investigate experimentally (adaptive)' }))
    .toHaveCount(0);
});
