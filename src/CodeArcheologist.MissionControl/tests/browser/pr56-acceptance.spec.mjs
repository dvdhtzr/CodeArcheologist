import { test, expect } from '@playwright/test';
import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises';
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
    const selectedBefore = await page.locator('.graph-focus h3').innerText();
    await relationships.first().locator('button').first().click();
    await expect(page.locator('.graph-focus h3')).not.toHaveText(selectedBefore);
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
});
