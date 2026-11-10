import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { join, dirname } from 'node:path';
import {
  AGENTS, ROLES, SCENES, SCENE_STATES, DEMO_EVENTS,
  stageFor, sceneIndex, nextScene, visibleEvents,
} from '../src/demo.ts';

const root = dirname(dirname(fileURLToPath(import.meta.url)));
const read = path => readFileSync(join(root, path), 'utf8');

test('role roster is exactly the first three authentic Gate C roles', () => {
  assert.deepEqual(ROLES, ['investigator', 'skeptic', 'reviewer']);
  for (const role of ROLES) {
    assert.equal(AGENTS[role].id, role);
    assert.ok(AGENTS[role].principle.length > 20);
  }
});

test('demonstration follows the sequential host-observed stage order', () => {
  assert.deepEqual(SCENES, ['ready', 'investigating', 'challenging', 'reviewing', 'complete']);
  assert.deepEqual(SCENES.map(scene => stageFor(scene, 'investigator')),
    ['queued', 'active', 'accepted', 'accepted', 'accepted']);
  assert.deepEqual(SCENES.map(scene => stageFor(scene, 'skeptic')),
    ['queued', 'queued', 'active', 'accepted', 'accepted']);
  assert.deepEqual(SCENES.map(scene => stageFor(scene, 'reviewer')),
    ['queued', 'queued', 'queued', 'active', 'accepted']);
  for (const scene of SCENES) {
    const states = SCENE_STATES[scene];
    if (states.reviewer !== 'queued') {
      assert.equal(states.investigator, 'accepted');
      assert.equal(states.skeptic, 'accepted');
    }
    if (states.skeptic !== 'queued') {
      assert.equal(states.investigator, 'accepted');
    }
  }
  assert.equal(nextScene('complete'), 'complete');
});

test('demo event history is deterministic, ordered, and strictly synthetic', () => {
  const ids = DEMO_EVENTS.map(e => e.id);
  assert.equal(new Set(ids).size, ids.length);
  const flattened = SCENES.flatMap(scene => DEMO_EVENTS.filter(event => event.scene === scene));
  assert.deepEqual(DEMO_EVENTS, flattened);
  for (const scene of SCENES) {
    const visible = visibleEvents(scene);
    assert.equal(visible.length, DEMO_EVENTS.filter(e => sceneIndex(e.scene) <= sceneIndex(scene)).length);
    assert.ok(visible.every(e => sceneIndex(e.scene) <= sceneIndex(scene)));
  }
  assert.equal(visibleEvents('ready').length, 1);
  assert.equal(visibleEvents('complete').length, DEMO_EVENTS.length);
  assert.ok(DEMO_EVENTS.every(e => e.actor === 'host' || ROLES.includes(e.actor)));
});

test('MC1 is isolated from backend and never claims a live connection', () => {
  const source = read('src/App.tsx');
  const vite = read('vite.config.ts');
  const markup = read('index.html');
  assert.ok(source.includes('API not connected'));
  assert.ok(source.includes('DEMO MODE'));
  assert.ok(source.includes('No real repository was read.') === false); // fixture detail, not a fetch
  assert.ok(source.includes('This playback is an explicitly synthetic design preview'));
  assert.ok(!/\\bfetch\\s*\\(/.test(source));
  assert.ok(!/new\\s+WebSocket|HubConnectionBuilder|EventSource/.test(source));
  assert.ok(!/CodeArcheologist\\.Api|CodeArcheologist\\.Client/.test(source));
  assert.ok(!/proxy\\s*:/.test(vite));
  assert.ok(vite.includes('5175'));
  assert.ok(markup.includes('Mission Control'));
});

test('character art, reduced motion and keyboard affordances are present', () => {
  const app = read('src/App.tsx');
  const art = read('src/AgentAvatar.tsx');
  const css = read('src/style.css');
  assert.ok(app.includes('aria-pressed'));
  assert.ok(app.includes('aria-live'));
  assert.ok(app.includes('aria-label="Reset demonstration"'));
  assert.ok(art.includes('useId()'));
  assert.ok(art.includes("role === 'investigator'"));
  assert.ok(art.includes("role === 'skeptic'"));
  assert.ok(art.includes("role === 'reviewer'"));
  assert.ok(css.includes('prefers-reduced-motion: reduce'));
  assert.ok(css.includes(':focus-visible'));
  assert.ok(css.includes('agent-node[data-state="active"]'));
});
