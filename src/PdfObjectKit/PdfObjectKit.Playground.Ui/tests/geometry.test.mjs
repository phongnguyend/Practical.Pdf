import { test } from 'node:test';
import assert from 'node:assert/strict';
import { containsPoint, hitTest } from '../src/geometry.mjs';

test('hit testing uses polygon geometry rather than its bounding rectangle', () => {
  const diamond = [[0, 10], [10, 0], [20, 10], [10, 20]];
  assert.equal(containsPoint(diamond, 10, 10), true);
  assert.equal(containsPoint(diamond, 1, 1), false);
});

test('viewport transform handles rotation/crop offsets and selects the topmost object', () => {
  const viewport = { convertToViewportPoint: (x, y) => [y - 20, x - 10] };
  const item = { corners: [{ x: 10, y: 20 }, { x: 30, y: 20 }, { x: 30, y: 40 }, { x: 10, y: 40 }] };
  const top = { ...item, id: 'top' };
  assert.equal(hitTest([item, top], viewport, 10, 10), top);
  assert.equal(hitTest([item], viewport, 40, 40), undefined);
});
