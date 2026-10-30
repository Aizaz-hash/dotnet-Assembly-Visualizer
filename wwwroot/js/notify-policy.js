/*
 * Rules for the small toast notifications (how long one stays, how many show at once). Pure functions so they can be tested;
 * the DOM part lives in app.js (showMessage).
 */
(function (root, factory) {
    if (typeof module === 'object' && module.exports) module.exports = factory();
    else root.NotifyPolicy = factory();
})(typeof self !== 'undefined' ? self : this, function () {
    'use strict';

    const MIN_MS = 2500, MAX_MS = 5000, MAX_VISIBLE = 3;

    function normalizeType(type) { return type === 'error' || type === 'success' ? type : 'info'; }

    /**
     * 2.5 to 5 seconds: long enough to read, short enough not to get in the way. A longer text and an error stay a little longer,
     * but never past the maximum - the full text of an error is always kept in the status bar and the console.
     */
    function toastDuration(type, text) {
        const base = normalizeType(type) === 'error' ? 4000 : 3000;
        const extra = Math.max(0, String(text || '').length - 60) * 20;
        return Math.min(MAX_MS, Math.max(MIN_MS, base + extra));
    }

    /** How many of the oldest toasts must go so that `incoming` more fit. */
    function overflow(currentCount, incoming) { return Math.max(0, currentCount + (incoming || 1) - MAX_VISIBLE); }

    /** A repeat of the same message must refresh the existing toast instead of stacking a copy. */
    function sameMessage(a, b) { return normalizeType(a.type) === normalizeType(b.type) && String(a.text) === String(b.text); }

    return { MIN_MS, MAX_MS, MAX_VISIBLE, normalizeType, toastDuration, overflow, sameMessage };
});
