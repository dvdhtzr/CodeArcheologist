import test from 'node:test';
import assert from 'node:assert/strict';
import { anchorAtlasLayout } from '../src/live/atlasLayout.ts';
import { isAtlasViewport } from '../src/live/atlasEvidence.ts';

test('Dagre presentation reflow aligns a surviving center without adding nodes or edges', () => {
  const previous = new Map([
    ['center', { x: 120, y: -40 }],
    ['old', { x: 280, y: -40 }],
  ]);
  const next = [
    { id: 'center', position: { x: 5, y: 35 }, data: { saved: true } },
    { id: 'new', position: { x: 280, y: 35 }, data: { saved: true } },
  ];
  const result = anchorAtlasLayout(next, previous, 'center');
  assert.deepEqual(result.map(node => node.id), ['center', 'new']);
  assert.deepEqual(result[0].position, previous.get('center'));
  assert.deepEqual(result[1].position, { x: 395, y: -40 });
  assert.deepEqual(next[0].position, { x: 5, y: 35 }, 'no mutation of the source layout');
  assert.equal(result.every(n => n.data.saved), true);
});
test('Layout anchoring does not invent a match when views share no saved IDs', () => {
  const next = [{ id: 'only', position: { x: 12, y: 20 } }];
  assert.deepEqual(anchorAtlasLayout(next, new Map([['foreign', { x: 999, y: 999 }]]), 'foreign'), next);
  assert.deepEqual(anchorAtlasLayout(next, new Map([['only', { x: 4, y: 8 }]]), 'not-visible')[0].position,
    { x: 4, y: 8 }, 'fallback is the first actual shared node');
});
test('Saved viewport restoration accepts finite positive zoom only', () => {
  assert.equal(isAtlasViewport({ x: -500, y: 100, zoom: 0.55 }), true);
  assert.equal(isAtlasViewport({ x: 0, y: 0, zoom: 0 }), false);
  assert.equal(isAtlasViewport({ x: Infinity, y: 0, zoom: 1 }), false);
  assert.equal(isAtlasViewport({ x: 0, y: NaN, zoom: 1 }), false);
  assert.equal(isAtlasViewport({ x: 0, y: 0, zoom: 20 }), false);
  assert.equal(isAtlasViewport(null), false);
});
