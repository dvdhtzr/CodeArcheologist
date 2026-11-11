import { test, expect } from '@playwright/test';
import { readFile, mkdtemp, rm, stat } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve, relative } from 'node:path';
import { startApi, stopApi, bootstrap, root } from '../../../../tests/api/harness.mjs';

// Opt-in only. Reuses pinned, owner-acquired real repos from C6c, never
// downloads, compiles, executes or modifies the repository under inspection.
// Default browser tests remain lightweight and entirely self-contained.
test('Atlas optional pinned curated repository: saved-fingerprint provenance and bounded navigation', async ({ page }) => {
  test.setTimeout(180_000);
  const curatedRoot = process.env.CODEARCHEOLOGIST_ATLAS_CURATED_ROOT;
  test.skip(!curatedRoot, 'Optional: supply CODEARCHEOLOGIST_ATLAS_CURATED_ROOT after running scripts/acquire-curated.ps1');
  const manifest = JSON.parse(await readFile(join(root, 'eval/curated/repositories.json'), 'utf8'));
  if (manifest.schemaVersion !== 'c6c-curated-v1') throw new Error('Unknown curated manifest schema');
  const id = process.env.CODEARCHEOLOGIST_ATLAS_CURATED_ID || 'R01';
  const entry = manifest.repositories.find(item => item.id === id);
  if (!entry) throw new Error('Requested curated case is not in the approved pinned manifest: ' + id);
  const base = resolve(curatedRoot, entry.id);
  const source = resolve(base, entry.subtree || '.');
  if (relative(base, source).startsWith('..') || !((await stat(source)).isDirectory()))
    throw new Error('Curated subtree is not a directory within its pinned checkout');

  const directory = await mkdtemp(join(tmpdir(), 'code-archeologist-atlas-curated-'));
  let api;
  try {
    api = await startApi(join(directory, 'api-data'), 'http://127.0.0.1:5175');
    await page.goto('/');
    await page.getByLabel('Pairing code (shown in the API window)').fill(bootstrap);
    await page.getByRole('button', { name: 'Connect', exact: true }).click();
    await expect(page.getByText('API connected')).toBeVisible();
    await page.getByPlaceholder('New workspace name').fill('Atlas pinned ' + entry.id);
    await page.getByRole('button', { name: 'Create', exact: true }).click();
    await page.getByPlaceholder('Local folder path to register').fill(source);
    await page.getByRole('button', { name: 'Register', exact: true }).click();

    const capture = page.waitForResponse(response =>
      response.url().endsWith('/snapshots') && response.request().method() === 'POST');
    await page.getByRole('button', { name: 'Capture new snapshot' }).click();
    const captured = await capture;
    expect(captured.ok(), 'The pinned repository must be capturable without 413 or live source changes').toBe(true);
    const snapshot = await captured.json();
    expect(snapshot.fingerprintSha256).toBe(entry.snapshotFingerprint);

    await page.getByRole('button', { name: 'Architecture Explorer' }).click();
    const graphResponse = page.waitForResponse(response =>
      response.url().endsWith('/csharp-evidence-graph') && response.request().method() === 'GET');
    const start = Date.now();
    await page.getByRole('button', { name: 'Load Architecture Atlas' }).click();
    const response = await graphResponse;
    expect(response.ok(), 'This optional repository must fit the existing authoritative graph bounds').toBe(true);
    const graph = await response.json();
    expect(graph.snapshotId).toBe(snapshot.id);
    expect(graph.fingerprintSha256).toBe(entry.snapshotFingerprint);
    await expect(page.locator('.atlas-viewport .react-flow')).toBeVisible();
    await expect(page.locator('.atlas-metrics')).toContainText('saved nodes');
    const initialLoadMs = Date.now() - start;

    const focus = graph.nodes.find(node => node.kind === 'syntax-declaration');
    expect(focus).toBeTruthy();
    await page.getByPlaceholder('Name, file, or declaration').fill(focus.label);
    await expect(page.locator('.atlas-search-results button').first()).toBeVisible();
    await page.locator('.atlas-search-results button').first().click();
    await expect(page.locator('.atlas-canvas-toolbar')).toContainText('Focused evidence neighborhood');

    const displayedIds = await page.locator('.atlas-viewport .react-flow__edge').evaluateAll(
      elements => elements.map(element => element.getAttribute('data-id')).filter(Boolean));
    const savedIds = new Set(graph.edges.map(edge => edge.id));
    expect(displayedIds.every(id => savedIds.has(id))).toBe(true);
    expect(displayedIds.length).toBeLessThanOrEqual(250);

    const report = { schemaVersion: 'atlas-curated-browser-v1',
      caseId: entry.id, pinnedCommit: entry.commitSha, pinnedFingerprint: entry.snapshotFingerprint,
      capturedFiles: entry.snapshotFileCount, savedNodes: graph.nodeCount, savedEdges: graph.edgeCount,
      renderedEdgeCount: displayedIds.length, initialLoadMs, capturedAtUtc: new Date().toISOString(),
      qualification: 'Owner-acquired pinned real repository; browser timing includes local API and graph build, not just rendering' };
    await test.info().attach('atlas-curated-browser-metrics.json',
      { body: Buffer.from(JSON.stringify(report, null, 2)), contentType: 'application/json' });
  } finally {
    await stopApi(api);
    await rm(directory, { recursive: true, force: true });
  }
});
