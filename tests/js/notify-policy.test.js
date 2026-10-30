const test = require('node:test');
const assert = require('node:assert/strict');
const N = require('../../wwwroot/js/notify-policy.js');

test('every toast stays between 2.5 and 5 seconds, whatever the text', () => {
    for (const type of ['info', 'success', 'error', 'nonsense', undefined]) {
        for (const text of ['', 'ok', 'x'.repeat(60), 'x'.repeat(200), 'x'.repeat(5000)]) {
            const ms = N.toastDuration(type, text);
            assert.ok(ms >= 2500 && ms <= 5000, `${type}/${text.length}: ${ms}`);
        }
    }
});

test('short information disappears quickly, errors and long texts stay longer, but never past the limit', () => {
    assert.equal(N.toastDuration('info', 'Parsed 13 types'), 3000);
    assert.ok(N.toastDuration('error', 'Scan failed') > N.toastDuration('info', 'Scan failed'));
    assert.ok(N.toastDuration('info', 'x'.repeat(150)) > N.toastDuration('info', 'short'));
    assert.equal(N.toastDuration('error', 'x'.repeat(5000)), 5000);
});

test('unknown types are shown as information', () => {
    assert.equal(N.normalizeType('warning'), 'info');
    assert.equal(N.normalizeType('error'), 'error');
    assert.equal(N.normalizeType('success'), 'success');
    assert.equal(N.normalizeType(undefined), 'info');
});

test('at most three toasts are visible: the oldest make room for a new one', () => {
    assert.equal(N.MAX_VISIBLE, 3);
    assert.equal(N.overflow(0, 1), 0);
    assert.equal(N.overflow(2, 1), 0);
    assert.equal(N.overflow(3, 1), 1);
    assert.equal(N.overflow(5, 1), 3);
});

test('the same message is recognised so it refreshes instead of stacking', () => {
    assert.ok(N.sameMessage({ type: 'info', text: 'a' }, { type: 'info', text: 'a' }));
    assert.ok(!N.sameMessage({ type: 'info', text: 'a' }, { type: 'error', text: 'a' }));
    assert.ok(!N.sameMessage({ type: 'info', text: 'a' }, { type: 'info', text: 'b' }));
    assert.ok(N.sameMessage({ type: 'weird', text: 'a' }, { type: 'info', text: 'a' }));      // an unknown type is information
});
