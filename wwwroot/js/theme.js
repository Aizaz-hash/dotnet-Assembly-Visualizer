/*
 * Light / Dark / System. The choice ("pref") is stored in this browser; "system" follows the operating system or browser setting and
 * changes with it. The result ("resolved": light or dark) is set as <html data-theme="..."> and every colour comes from the CSS
 * variables in css/theme.css. The graphs are drawn by vis-network, which takes colours as values, so Theme.colors() reads the same
 * variables for them. The pure rules (normalize, resolve) are testable without a browser.
 */
(function (root, factory) {
    if (typeof module === 'object' && module.exports) { module.exports = factory(null); module.exports.create = factory; }   // create(window): for tests with a fake window
    else root.Theme = factory(root);
})(typeof self !== 'undefined' ? self : this, function (win) {
    'use strict';

    const KEY = 'av.theme';
    const PREFS = ['system', 'light', 'dark'];

    /** Anything that is not light or dark means "system" (also a missing or damaged stored value). */
    function normalize(pref) { return pref === 'light' || pref === 'dark' ? pref : 'system'; }

    /** What the page uses: an explicit choice wins, "system" follows the system setting. */
    function resolve(pref, systemDark) {
        const p = normalize(pref);
        return p === 'system' ? (systemDark ? 'dark' : 'light') : p;
    }

    const api = { KEY, PREFS, normalize, resolve };
    if (!win || typeof win.document === 'undefined') return api;

    const doc = win.document;
    const mq = typeof win.matchMedia === 'function' ? win.matchMedia('(prefers-color-scheme: dark)') : null;
    let cache = null;

    function stored() { try { return win.localStorage.getItem(KEY); } catch { return null; } }
    function pref() { return normalize(stored()); }

    // Sets the page to a preference (light / dark / system) and tells the rest of the page.
    function applyWith(p) {
        const resolved = resolve(p, !!(mq && mq.matches));
        doc.documentElement.dataset.theme = resolved;
        doc.documentElement.dataset.themePref = p;
        cache = null;
        doc.dispatchEvent(new win.CustomEvent('themechange', { detail: { pref: p, resolved } }));
        return resolved;
    }
    const apply = () => applyWith(pref());

    // The page keeps working when nothing can be stored: the choice then lasts until it is reloaded.
    function set(value) {
        const p = normalize(value);
        try { win.localStorage.setItem(KEY, p); } catch { /* not remembered */ }
        return applyWith(p);
    }

    // "system": react when the operating system / browser switches between light and dark
    if (mq) {
        const onChange = () => { if ((doc.documentElement.dataset.themePref || pref()) === 'system') applyWith('system'); };
        if (mq.addEventListener) mq.addEventListener('change', onChange); else if (mq.addListener) mq.addListener(onChange);
    }

    const v = (cs, name) => cs.getPropertyValue('--g-' + name).trim();
    const set4 = (cs, n) => ({ background: v(cs, n + '-bg'), border: v(cs, n + '-bd'), highlight: { background: v(cs, n + '-hbg'), border: v(cs, n + '-hbd') } });

    /** The colours the graphs use, read from the active theme (cached until the theme changes). */
    function colors() {
        if (cache) return cache;
        const cs = win.getComputedStyle(doc.documentElement);
        cache = {
            canvas: v(cs, 'canvas'),
            cls: set4(cs, 'cls'), iface: set4(cs, 'if'), ext: set4(cs, 'ext'), ok: set4(cs, 'ok'), good: set4(cs, 'good'), bad: set4(cs, 'bad'),
            selBd: v(cs, 'sel-bd'), selHbd: v(cs, 'sel-hbd'), archOkBd: v(cs, 'arch-ok-bd'),
            font: v(cs, 'font'), fontExt: v(cs, 'font-ext'), fontBad: v(cs, 'font-bad'),
            edge: v(cs, 'edge'), edgeH: v(cs, 'edge-h'), edgeM: v(cs, 'edge-m'), edgeMH: v(cs, 'edge-m-h'), edgeA: v(cs, 'edge-a'),
            edgeBad: v(cs, 'edge-bad'), edgeBadH: v(cs, 'edge-bad-h'), label: v(cs, 'label'), labelStroke: v(cs, 'label-stroke')
        };
        return cache;
    }

    return Object.assign(api, { pref, apply, set, colors });
});
