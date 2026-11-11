import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { startApi, stopApi, bootstrap, client } from '../../../tests/api/harness.mjs';
import { ApiClient } from '../src/live/api.ts';
import { LiveRun } from '../src/live/realtime.ts';
import { hasGap, liveStages } from '../src/live/ledger.ts';

// MC3 end to end: the real adapter (REST + @microsoft/signalr) against a real,
// disposable API process and a never-compiled fixture. Requires the Release
// API build (dotnet build src/CodeArcheologist.Api -c Release). The run is
// cancelled at once, so it ends quickly whether or not Ollama is running.

test('MC3 adapter drives a real investigation feed end to end', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'code-archeologist-mc3-'));
  const source = join(directory, 'source');
  await mkdir(source);
  await writeFile(join(source, 'Controller.cs'),
    'namespace MC3;\n[Route("api/x")]\npublic class XController\n{\n    [HttpGet]\n    public int Get() => Help();\n    private int Help() => 1;\n}\n');
  let api;
  let feed;
  try {
    api = await startApi(join(directory, 'data'));
    const origin = { Origin: client }; // a browser sets this itself
    const caClient = new ApiClient('http://127.0.0.1:5127', globalThis.fetch, origin);
    await assert.rejects(caClient.pair('0'.repeat(32)), /pairing code/);
    await caClient.pair(bootstrap);

    const workspace = await caClient.createWorkspace('MC3');
    assert.ok((await caClient.listWorkspaces()).items.some(w => w.id === workspace.id));
    const repository = await caClient.registerRepository(workspace.id, source);
    const snapshot = await caClient.captureSnapshot(workspace.id, repository.id);
    const scope = { workspaceId: workspace.id, repositoryId: repository.id, snapshotId: snapshot.id };
    const entries = await caClient.listEntryPoints(scope);
    assert.equal(entries.count, 1);
    assert.equal(entries.items[0].evidenceStatus, 'candidate');

    const started = await caClient.startInvestigation(scope, entries.items[0].id);
    const states = [];
    feed = new LiveRun(caClient, scope, started.runId, state => states.push(state), origin);
    await feed.start();
    assert.equal(feed.state.connection, 'live', 'hub subscription: ' + feed.state.error);

    try { await caClient.cancel(scope, started.runId, 1); } catch (error) { assert.equal(error.status, 409); }
    const deadline = Date.now() + 60000;
    while (!/^(completed|incomplete|interrupted|cancelled|rejected|failed)$/.test(feed.state.execution?.currentAttempt?.status ?? '')) {
      if (Date.now() > deadline) throw new Error('run did not end: ' + JSON.stringify(feed.state).slice(0, 600));
      await new Promise(r => setTimeout(r, 200));
    }
    await feed.resync();

    const rest = await caClient.events(scope, started.runId);
    assert.deepEqual(feed.state.events.map(e => e.eventId), rest.map(e => e.eventId), 'the view equals the REST ledger');
    assert.equal(hasGap(feed.state.events), false);
    assert.equal(feed.state.events[0].eventType, 'attempt-admitted');
    assert.ok(states.some(s => s.events.length > 0 && s.connection === 'live'), 'pushed updates reached the view');
    const stages = liveStages(feed.state.execution);
    assert.ok(Object.values(stages).every(s => typeof s.hostStatus === 'string'));
    assert.notEqual(stages.reviewer.state, 'accepted', 'a cancelled or failed run never shows an accepted Reviewer');

    await feed.stop();
    feed = null;
    await caClient.revoke();
    assert.equal(caClient.connected, false);
    const reuse = new ApiClient('http://127.0.0.1:5127', globalThis.fetch, origin);
    await assert.rejects(reuse.listWorkspaces(), /Not connected/);
  } finally {
    await feed?.stop();
    await stopApi(api);
    await rm(directory, { recursive: true, force: true });
  }
});
