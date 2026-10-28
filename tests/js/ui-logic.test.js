const test = require('node:test');
const assert = require('node:assert/strict');
const { rowClickAction, loaderStage } = require('../../wwwroot/js/ui-logic.js');

test('single click selects and remembers the open state', () => {
    const a = rowClickAction(1, null, 'k', false, true);
    assert.equal(a.select, true);
    assert.equal(a.toggleTo, null);
    assert.deepEqual(a.pending, { key: 'k', wasOpen: false });
});

test('double-click on a collapsed node expands it and selects only once', () => {
    const first = rowClickAction(1, null, 'k', false, true);
    const second = rowClickAction(2, first.pending, 'k', false, true);
    assert.equal(second.select, false);
    assert.equal(second.toggleTo, 'expand');
    assert.equal(second.pending, null);
});

test('double-click on an expanded node collapses it', () => {
    const first = rowClickAction(1, null, 'k', true, true);
    assert.equal(rowClickAction(2, first.pending, 'k', true, true).toggleTo, 'collapse');
});

test('selecting may auto-expand the row; the double-click still expands (uses the pre-click state)', () => {
    const first = rowClickAction(1, null, 'k', false, true);          // was collapsed
    const second = rowClickAction(2, first.pending, 'k', true, true);  // now open because select expanded it
    assert.equal(second.toggleTo, 'expand');                          // idempotent expand, never an undo
});

test('double-click on a leaf does nothing extra and never selects twice', () => {
    const first = rowClickAction(1, null, 'leaf', false, false);
    const second = rowClickAction(2, first.pending, 'leaf', false, false);
    assert.equal(second.select, false);
    assert.equal(second.toggleTo, null);
});

test('a second click on a different node is not treated as a double-click toggle', () => {
    const first = rowClickAction(1, null, 'a', false, true);
    assert.equal(rowClickAction(2, first.pending, 'b', false, true).toggleTo, null);
});

test('loader: upload shows a real percentage, later stages are indeterminate', () => {
    assert.deepEqual(loaderStage('upload', 0.5), { text: 'Uploading… 50%', fraction: 0.5 });
    for (const p of ['parse', 'read', 'build']) assert.equal(loaderStage(p).fraction, null);
    assert.equal(loaderStage('nonsense').text, '');
});
