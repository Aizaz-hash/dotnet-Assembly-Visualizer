/*
 * Small pure decisions the UI makes, kept out of app.js so they can be unit-tested with node --test.
 */
(function (root, factory) {
    if (typeof module === 'object' && module.exports) module.exports = factory();
    else root.UiLogic = factory();
})(typeof self !== 'undefined' ? self : this, function () {
    'use strict';

    /**
     * What a click on a tree row should do. A double-click arrives as two clicks (event.detail 1, then 2).
     *  - detail 1: select (as before) and remember whether the node was open BEFORE selecting
     *  - detail >= 2: never select again; expand a node that was collapsed, collapse one that was open
     *    (using the remembered state, because selecting may have rebuilt the tree and auto-expanded the row)
     * Returns { select, toggleTo: 'expand' | 'collapse' | null, pending }.
     */
    function rowClickAction(detail, pending, nodeKey, isOpen, hasChildren) {
        if (detail >= 2) {
            let toggleTo = null;
            if (hasChildren && pending && pending.key === nodeKey) toggleTo = pending.wasOpen ? 'collapse' : 'expand';
            return { select: false, toggleTo, pending: null };
        }
        return { select: true, toggleTo: null, pending: { key: nodeKey, wasOpen: !!isOpen } };
    }

    /** Loader overlay model: which stages show a real percentage and which are indeterminate. */
    function loaderStage(phase, fraction) {
        switch (phase) {
            case 'upload': return { text: `Uploading… ${Math.round(fraction * 100)}%`, fraction };
            case 'parse': return { text: 'Parsing the assembly (types, methods, calls)…', fraction: null };
            case 'read': return { text: 'Reading the result…', fraction: null };
            case 'build': return { text: 'Building the explorer and first view…', fraction: null };
            default: return { text: '', fraction: null };
        }
    }

    return { rowClickAction, loaderStage };
});
