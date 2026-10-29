import { test } from 'node:test';
import assert from 'node:assert/strict';
import { formatMetadataDate, metadataDateToIso } from '../src/metadataDates.mjs';

test('PDF dates are readable with their original offsets and precision', () => {
  assert.equal(formatMetadataDate("D:20260402114420+07'00'"), '2026-04-02 11:44:20 +07:00');
  assert.equal(formatMetadataDate("D:20260402114420-05'30'"), '2026-04-02 11:44:20 -05:30');
  assert.equal(formatMetadataDate('D:20260402114420Z'), '2026-04-02 11:44:20 +00:00');
  assert.equal(formatMetadataDate('D:202604'), '2026-04');
  assert.equal(formatMetadataDate('D:2026'), '2026');
  assert.equal(formatMetadataDate('2026-04-02T11:44:20+07:00'), '2026-04-02 11:44:20 +07:00');
  assert.equal(formatMetadataDate('unknown'), 'unknown');
});

test('edited dates serialize to ISO without shifting the timezone', () => {
  assert.equal(metadataDateToIso('2026-04-02 11:44:20 +07:00'), '2026-04-02T11:44:20+07:00');
  assert.throws(() => metadataDateToIso('2026-04'));
  assert.throws(() => metadataDateToIso('2026-04-02 11:44:20'));
});
