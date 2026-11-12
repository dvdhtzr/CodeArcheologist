import { test, expect } from '@playwright/test';
import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { startApi, stopApi, bootstrap } from '../../../../tests/api/harness.mjs';

// PR #56 browser acceptance. A disposable API database and source tree are
// created on every run. This test never connects to the owner's regular data,
// runs a model, or evaluates a repository's code.
const browserOrigin = 'http://127.0.0.1:5175';
let directory;
let source;
let api;

const fixture = versionMethod => `namespace BrowserAcceptance;
[Route("api/test")]
public class SampleController
{
    [HttpGet]
    public int Get() => Helper();
    private int Helper() => 42;
    private int ${versionMethod}() => 1;
}
`;

const select = (page, label) =>
  page.getByRole('combobox', { name: new RegExp(label, 'i') });

test.beforeAll(async () => {
  directory = await mkdtemp(join(tmpdir(), 'code-archeologist-browser-'));
  source = join(directory, 'repository');
  await mkdir(source);
  // A real policy exclusion must be visible in snapshot coverage, not invented by the UI.
  await mkdir(join(source, 'node_modules'));
  await writeFile(join(source, 'node_modules', 'ignored.js'), 'do not analyze');
  await writeFile(join(source, 'SampleController.cs'), fixture('LegacyOnly'));
  api = await startApi(join(directory, 'api-data'), browserOrigin);
});

test.afterAll(async () => {
  await stopApi(api);
  if (directory) await rm(directory, { recursive: true, force: true });
});

test('PR #56: real browser acceptance of verified evidence graph and snapshot isolation', async ({ page }) => {
  test.setTimeout(120_000);

  await test.step('Pair and choose an isolated workspace, repository, and snapshot', async () => {
    await page.goto('/');
    await expect(page.getByRole('heading', { name: 'Run an investigation' })).toBeVisible();
    await page.getByLabel('Pairing code (shown in the API window)').fill(bootstrap);
    await page.getByRole('button', { name: 'Connect', exact: true }).click();
    await expect(page.getByText('API connected')).toBeVisible();

    await page.getByPlaceholder('New workspace name').fill('Browser Acceptance');
    await page.getByRole('button', { name: 'Create', exact: true }).click();
    await expect(select(page, 'Workspace')).not.toHaveValue('');

    await page.getByPlaceholder('Local folder path to register').fill(source);
    await page.getByRole('button', { name: 'Register', exact: true }).click();
    await expect(select(page, 'Repository')).not.toHaveValue('');

    await page.getByRole('button', { name: 'Capture new snapshot' }).click();
    await expect(select(page, 'Snapshot')).not.toHaveValue('');
  });

  await test.step('R2 PR1: Architecture Atlas renders saved nodes, search, focus and provenance', async () => {
    await page.getByRole('button', { name: 'Architecture Explorer' }).click();
    await expect(page.getByRole('button', { name: 'Architecture Atlas', exact: true }))
      .toHaveAttribute('aria-pressed', 'true');
    await page.getByRole('button', { name: 'Load Architecture Atlas' }).click();
    await expect(page.locator('.atlas-metrics')).toContainText('saved nodes');
    await expect(page.locator('.atlas-provenance')).toContainText('csharp-snapshot-evidence-graph-v8');
    await expect(page.locator('.atlas-viewport .react-flow')).toBeVisible();
    await expect(page.locator('.atlas-viewport .react-flow__node').first()).toBeVisible();
    await expect(page.locator('.atlas-legend')).toContainText('Provisional local');
    await expect(page.locator('.atlas-legend')).toContainText('Candidate only');

    // R2 PR3: keyboard-operable inspector sizing and the reversible search panel.
    const panelWidth = page.getByRole('slider', { name: 'Evidence panel width' });
    await expect(panelWidth).toHaveValue('360');
    await panelWidth.focus();
    await panelWidth.press('End');
    await expect(panelWidth).toHaveValue('540');
    await expect(page.locator('.atlas-workspace')).toHaveCSS('--atlas-inspector-width', '540px');
    await page.getByRole('button', { name: 'Hide search panel' }).click();
    await expect(page.locator('#atlas-search-panel')).toBeHidden();
    await expect(page.locator('.atlas-viewport')).toBeVisible();
    await page.getByRole('button', { name: 'Show search panel' }).click();
    await expect(page.locator('#atlas-search-panel')).toBeVisible();

    // Find a declaration that is not in the project/file-only overview.
    await page.getByPlaceholder('Name, file, or declaration').fill('Helper');
    const declaration = page.locator('.atlas-search-results button')
      .filter({ hasText: 'syntax-declaration' }).first();
    await expect(declaration).toBeVisible();
    await declaration.click();
    await expect(page.locator('.atlas-canvas-toolbar')).toContainText('Focused evidence neighborhood');
    await expect(page.locator('.atlas-details .atlas-selected-label')).toContainText('Helper');
    const focusedMethodId = await page.locator('.atlas-viewport .react-flow__node.selected')
      .first().getAttribute('data-id');
    expect(focusedMethodId).toBeTruthy();
    // SVG <g> wrappers can fail Playwright's element-visibility check even
    // when a valid edge path exists. Assert the actual rendered path data,
    // the nonzero host-projected relationship count, and accessible selection.
    const paths = page.locator('.atlas-viewport .react-flow__edge-path');
    await expect(paths.first()).toHaveAttribute('d', /[MLCQ]/);
    await expect(page.locator('.atlas-relationships > button').first()).toBeVisible();
    await expect(page.locator('.atlas-metrics')).toContainText('visible / eligible');

    // Focused Helper has a recorded declared-in edge to the fixture file.
    // Selecting an arbitrary first non-selected SVG node can select the old
    // center while React Flow is reconciling selection state.
    const fileNeighbor = page.locator('.atlas-viewport .react-flow__node')
      .filter({ hasText: 'SampleController.cs' }).first();
    await expect(fileNeighbor).toBeVisible();
    await fileNeighbor.click();
    await expect(page.locator('.atlas-details .atlas-selected-label')).toContainText('SampleController.cs');
    await expect(page.getByRole('button', { name: "Expand this node's recorded neighbors" })).toBeEnabled();
    await page.getByRole('button', { name: "Expand this node's recorded neighbors" }).click();
    await expect(page.locator('.atlas-scope-note').last()).toContainText('Expansion seeds: 2 of 13');

    // Keyboard Enter activates the exact focused saved graph node, without
    // an aggregate selection callback overriding it with a previous node.
    const helperNode = page.getByTestId('rf__node-' + focusedMethodId);
    await expect(helperNode).toBeVisible();
    await helperNode.focus();
    await helperNode.press('Enter');
    await expect(page.locator('.atlas-details .atlas-selected-label')).toContainText('Helper');

    // R2 PR3: expansion only changes the saved subgraph, never auto-fits the camera.
    await page.waitForTimeout(200);
    const beforeInspectTransform = await page.locator('.atlas-viewport .react-flow__viewport')
      .evaluate(el => getComputedStyle(el).transform);
    await page.locator('.atlas-relationships > button').first().click();
    await expect(page.locator('.atlas-viewport .react-flow__viewport'))
      .toHaveCSS('transform', beforeInspectTransform);

    // PR2: inspect an actual declaration->file edge, including API-rehashed
    // saved source, then navigate the exact endpoint and use Back to restore
    // the original selected edge. No model output or synthetic graph data.
    await page.getByPlaceholder('Name, file, or declaration').fill('Helper');
    await page.locator('.atlas-search-results button')
      .filter({ hasText: 'syntax-declaration' }).first().click();
    await page.locator('.atlas-relationships > button')
      .filter({ hasText: 'declared-in' }).first().click();
    await expect(page.locator('.atlas-details')).toContainText('observed-location');
    await expect(page.locator('.atlas-endpoints')).toContainText('Go to source node');
    await expect(page.locator('.atlas-endpoints')).toContainText('Go to target node');
    await expect(page.locator('.atlas-details')).toContainText('Edge ID:');
    await page.getByRole('button', { name: 'Inspect edge source' }).click();
    const sourceEvidence = page.getByRole('region', { name: 'Atlas verified saved source' });
    await expect(sourceEvidence).toBeVisible();
    await expect(sourceEvidence).toContainText('private int Helper()');
    await expect(sourceEvidence).toContainText('Saved file SHA-256');
    await expect(sourceEvidence.locator('.atlas-source-line.is-focus')).toContainText('Helper');

    await page.locator('.atlas-project-evidence summary').click();
    await expect(page.locator('.atlas-project-evidence')).toContainText('Static project membership');
    await expect(page.locator('.atlas-project-evidence')).toContainText('not verified compiler inputs');
    await expect(page.locator('.atlas-project-evidence')).toContainText(
      'Project candidate details were not supplied');

    await page.waitForTimeout(200);
    const beforeNavigation = await page.locator('.atlas-viewport .react-flow__viewport').evaluate(el => {
      const matrix = new DOMMatrixReadOnly(getComputedStyle(el).transform);
      return { x: matrix.m41, y: matrix.m42, zoom: matrix.a };
    });
    await page.getByRole('button', { name: /Go to target node/ }).click();
    await expect(page.locator('.atlas-details .atlas-selected-label')).toContainText('SampleController.cs');
    await expect(page.getByRole('button', { name: 'Back to previous Atlas view' })).toBeEnabled();
    await page.getByRole('button', { name: 'Back to previous Atlas view' }).click();
    await expect(page.locator('.atlas-details .atlas-selected-label')).toContainText('declared-in');
    await expect.poll(async () => {
      const next = await page.locator('.atlas-viewport .react-flow__viewport').evaluate(el => {
        const matrix = new DOMMatrixReadOnly(getComputedStyle(el).transform);
        return { x: matrix.m41, y: matrix.m42, zoom: matrix.a };
      });
      return Math.abs(next.x - beforeNavigation.x) < 2 &&
        Math.abs(next.y - beforeNavigation.y) < 2 &&
        Math.abs(next.zoom - beforeNavigation.zoom) < 0.02;
    }).toBe(true);
    await expect(page.locator('.atlas-endpoints')).toContainText('Go to target node');
    // Returning to the prior selection must not resurrect its old source panel.
    await expect(page.locator('.atlas-inline-source')).toHaveCount(0);

    await page.getByRole('button', { name: /Go to source node/ }).click();
    await expect(page.locator('.atlas-details .atlas-selected-label')).toContainText('Helper');
    await page.getByRole('button', { name: 'Inspect saved source' }).click();
    await expect(page.getByRole('region', { name: 'Atlas verified saved source' }))
      .toContainText('private int Helper()');
    await page.getByRole('button', { name: 'Back to previous Atlas view' }).click();
    await expect(page.locator('.atlas-details .atlas-selected-label')).toContainText('declared-in');

    // The Atlas remains readable when narrow without changing selected evidence.
    await page.setViewportSize({ width: 820, height: 760 });
    await expect(page.locator('.atlas-viewport')).toBeVisible();
    const canvasBox = await page.locator('.atlas-viewport').boundingBox();
    const detailsBox = await page.locator('.atlas-details').boundingBox();
    expect(canvasBox && detailsBox && detailsBox.y > canvasBox.y).toBeTruthy();
    await page.setViewportSize({ width: 1280, height: 720 });

    await page.getByRole('button', { name: 'Project / file overview' }).click();
    await expect(page.locator('.atlas-canvas-toolbar')).toContainText('Project / file overview');
    await page.getByRole('button', { name: 'Agent Observatory' }).click();
  });

  await test.step('Load and filter the graph without Ollama', async () => {
    await page.getByRole('button', { name: 'Architecture Explorer' }).click();
    await expect(page.getByRole('heading', { name: 'Explore the code you captured.' })).toBeVisible();
    await page.getByRole('button', { name: 'Evidence graph', exact: true }).click();
    await page.getByRole('button', { name: 'Load saved evidence graph' }).click();

    await expect(page.locator('.graph-explorer .deterministic-summary strong').first()).not.toHaveText('0');
    await expect(page.locator('.graph-explorer .deterministic-summary strong').nth(1)).not.toHaveText('0');
    await expect(page.locator('.graph-kind-overview')).toContainText('syntax-declaration');
    await expect(page.locator('.graph-node-results .graph-node-choice').first()).toBeVisible();

    // A real node-kind chip must filter the list, and a text search must find a method.
    await page.locator('.graph-kind-overview button').filter({ hasText: 'syntax-declaration' }).click();
    await expect(page.getByRole('combobox', { name: 'Node kind' })).toHaveValue('syntax-declaration');
    await page.getByPlaceholder('Label, kind, or file path').fill('Helper');
    await expect(page.locator('.graph-node-results .graph-node-choice')).toHaveCount(1);
  });

  await test.step('Inspect preserved edge status and navigate to a neighbor', async () => {
    await page.locator('.graph-node-results .graph-node-choice').filter({ hasText: 'Helper' }).click();
    const relationships = page.locator('.graph-relation-list > li');
    await expect(relationships.first()).toBeVisible();
    await expect(page.locator('.graph-orbit')).toBeVisible();
    await expect(page.locator('.graph-relation-list')).toContainText(/observed-syntax|provisional-local/);
    await expect(relationships.first().locator('.graph-direction')).toContainText(/selected|neighbor/i);
    const selectedBefore = await page.locator('.graph-focus > h3').first().innerText();
    await relationships.first().locator('button').first().click();
    await expect(page.locator('.graph-focus > h3').first()).not.toHaveText(selectedBefore);
  });

  await test.step('Inspect hash-verified saved source for a node and an edge', async () => {
    // Recenter on the method declaration, not a similarly named invocation.
    // Neighbor navigation deliberately resets search and kind filters.
    await page.getByRole('combobox', { name: 'Node kind' }).selectOption('syntax-declaration');
    await page.getByPlaceholder('Label, kind, or file path').fill('Helper');
    const method = page.locator('.graph-node-results .graph-node-choice');
    await expect(method).toHaveCount(1);
    await method.click();
    await page.getByRole('button', { name: 'Inspect verified source' }).click();
    await expect(page.getByRole('heading', { name: 'Verified saved source' })).toBeVisible();
    await expect(page.locator('.deterministic-source pre')).toContainText('Helper');
    await expect(page.locator('.deterministic-source')).toContainText('SHA-256');
    await expect(page.locator('.deterministic-source')).toContainText('SampleController.cs');

    const sourceButton = page.locator('.graph-relation-list button:enabled')
      .filter({ hasText: 'Inspect edge source' }).first();
    await expect(sourceButton).toBeVisible();
    await sourceButton.click();
    await expect(page.locator('.deterministic-source pre')).toContainText(/Helper|Get/);
    await expect(page.locator('.deterministic-source')).toContainText('SHA-256');
  });

  await test.step('Navigate from a graph method into direct-impact analysis', async () => {
    await page.getByRole('button', { name: 'Inspect direct impact' }).click();
    await expect(page.getByRole('heading', { name: /Direct impact candidates:/ })).toBeVisible();
    await expect(page.locator('.deterministic-impact')).toContainText(/one-hop|provisional-local/i);
  });

  await test.step('Switch to a second saved snapshot without retaining stale evidence', async () => {
    await page.getByRole('button', { name: 'Agent Observatory' }).click();
    const firstSnapshot = await select(page, 'Snapshot').inputValue();
    // Change ONLY the temporary source, then explicitly capture again via the UI.
    await writeFile(join(source, 'SampleController.cs'), fixture('NewVersionOnly'));
    await page.getByRole('button', { name: 'Capture new snapshot' }).click();
    await expect(select(page, 'Snapshot')).not.toHaveValue(firstSnapshot);

    await page.getByRole('button', { name: 'Architecture Explorer' }).click();
    await expect(page.getByRole('heading', { name: 'Verified saved source' })).toHaveCount(0);
    await page.getByRole('button', { name: 'Architecture Atlas', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Load Architecture Atlas' })).toBeVisible();
    await expect(page.locator('.atlas-viewport')).toHaveCount(0);
    await page.getByRole('button', { name: 'Evidence graph', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Load saved evidence graph' })).toBeVisible();
    await expect(page.locator('.graph-focus')).toHaveCount(0);
    await page.getByRole('button', { name: 'Load saved evidence graph' }).click();
    await page.getByPlaceholder('Label, kind, or file path').fill('NewVersionOnly');
    await expect(page.locator('.graph-node-results .graph-node-choice')).toHaveCount(1);
    await page.getByPlaceholder('Label, kind, or file path').fill('LegacyOnly');
    await expect(page.locator('.graph-node-results .graph-node-choice')).toHaveCount(0);
    await expect(page.getByText('No matching nodes. An empty result is not evidence that a declaration or dependency is absent.')).toBeVisible();
  });

  await test.step('Existing Observatory and Findings navigation still works', async () => {
    await page.getByRole('button', { name: 'Agent Observatory' }).click();
    await expect(page.getByRole('heading', { name: 'Run an investigation' })).toBeVisible();
    await page.getByRole('button', { name: 'Findings', exact: true }).first().click();
    await expect(page.locator('.breadcrumb')).toContainText('Findings');
    await page.getByRole('button', { name: 'Architecture Explorer' }).click();
    await expect(page.getByRole('heading', { name: 'Explore the code you captured.' })).toBeVisible();
  });

  await test.step('Snapshot coverage reveals actual captured files and policy exclusions', async () => {
    await page.getByRole('button', { name: 'Snapshot coverage', exact: true }).click();
    await page.getByRole('button', { name: 'Load snapshot coverage' }).click();
    await expect(page.getByRole('heading', { name: 'What was actually captured?' })).toBeVisible();
    await expect(page.locator('.snapshot-coverage .deterministic-summary')).toContainText('Captured files');
    await expect(page.locator('.snapshot-coverage')).toContainText('path-and-extension-only');
    await expect(page.locator('.snapshot-coverage')).toContainText('SampleController.cs');
    await page.locator('.snapshot-coverage details').filter({ hasText: 'Excluded entries' }).locator('summary').click();
    await expect(page.locator('.snapshot-coverage')).toContainText('node_modules');
    await expect(page.locator('.snapshot-coverage')).toContainText('ignored-directory');
    await expect(page.locator('.snapshot-coverage')).toContainText('Classification method');
    const visibleHash = await page.locator('.snapshot-coverage').textContent();
    expect(visibleHash).toMatch(/Saved fingerprint:.*[0-9a-f]{64}/);

    await page.getByRole('button', { name: 'Agent Observatory' }).click();
    const snapshotSelect = select(page, 'Snapshot');
    const selectedSnapshot = await snapshotSelect.inputValue();
    const alternative = await snapshotSelect.locator('option').evaluateAll(
      (options, current) => options.map(o => o.value).find(v => v && v !== current), selectedSnapshot);
    expect(alternative).toBeTruthy();
    await snapshotSelect.selectOption(alternative);
    await page.getByRole('button', { name: 'Architecture Explorer' }).click();
    // R3 PR2 retains the Explorer across page navigation, but switching saved
    // snapshots MUST discard the entire previous Explorer state, including
    // the previously selected Coverage tab and its verified manifest.
    await expect(page.getByRole('button', { name: 'Architecture Atlas', exact: true }))
      .toHaveAttribute('aria-pressed', 'true');
    await expect(page.getByRole('button', { name: 'Load Architecture Atlas' })).toBeVisible();
    await expect(page.locator('.coverage-files')).toHaveCount(0);
    await page.getByRole('button', { name: 'Snapshot coverage', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Snapshot coverage', exact: true }))
      .toHaveAttribute('aria-pressed', 'true');
    await expect(page.getByRole('button', { name: 'Load snapshot coverage' })).toBeVisible();
    await expect(page.locator('.coverage-files')).toHaveCount(0);
  });
});

test('PR3: revoked token disconnects every tab and offers re-pairing', async ({ page }) => {
  test.setTimeout(90_000);
  await page.goto('/');
  const paired = page.waitForResponse(response =>
    response.url().endsWith('/api/v1/auth/token') && response.request().method() === 'POST');
  await page.getByLabel('Pairing code (shown in the API window)').fill(bootstrap);
  await page.getByRole('button', { name: 'Connect', exact: true }).click();
  const pairing = await paired;
  expect(pairing.ok()).toBeTruthy();
  const { accessToken } = await pairing.json();
  expect(accessToken).toMatch(/^[0-9a-f]{64}$/);

  await page.getByPlaceholder('New workspace name').fill('Revocation browser test');
  await page.getByRole('button', { name: 'Create', exact: true }).click();
  await expect(select(page, 'Workspace')).not.toHaveValue('');
  await page.getByPlaceholder('Local folder path to register').fill(source);
  await page.getByRole('button', { name: 'Register', exact: true }).click();
  await expect(select(page, 'Repository')).not.toHaveValue('');
  await page.getByRole('button', { name: 'Capture new snapshot' }).click();
  await expect(select(page, 'Snapshot')).not.toHaveValue('');
  await page.getByRole('button', { name: 'Architecture Explorer' }).click();
  await expect(page.getByRole('heading', { name: 'Explore the code you captured.' })).toBeVisible();
  await page.getByRole('button', { name: 'Snapshot coverage', exact: true }).click();

  // Actual API revocation, not a mocked 401. The next snapshot request must
  // cause one client-wide disconnected state even though Observatory is hidden.
  const revoked = await page.request.post('http://127.0.0.1:5127/api/v1/auth/token/revoke', {
    headers: { Origin: 'http://127.0.0.1:5175', Authorization: 'Bearer ' + accessToken,
      'Idempotency-Key': randomUUID() }, data: {},
  });
  expect(revoked.status()).toBe(204);
  await page.getByRole('button', { name: 'Load snapshot coverage' }).click();
  await expect(page.getByText('API not connected')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Connect to local API' })).toBeVisible();
  await expect(page.locator('.snapshot-coverage .deterministic-summary')).toHaveCount(0);
  await page.getByRole('button', { name: 'Connect to local API' }).click();
  await expect(page.getByLabel('Pairing code (shown in the API window)')).toBeVisible();
  await expect(page.locator('.live-form .live-error')).toContainText(/Session expired|not authorized|revoked/i);
});
