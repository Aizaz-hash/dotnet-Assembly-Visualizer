// Light / Dark / System: the rules, that every colour of the interface is a theme token, and that text stays readable in both themes.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const Theme = require('../../wwwroot/js/theme.js');

const root = path.join(__dirname, '..', '..', 'wwwroot');
const read = (...p) => fs.readFileSync(path.join(root, ...p), 'utf8').replace(/\r\n/g, '\n');

// ---------------------------------------------------------------- the rules

test('anything that is not light or dark means system, including a damaged stored value', () => {
    for (const bad of [null, undefined, '', 'blue', 'DARK', 42, {}]) assert.equal(Theme.normalize(bad), 'system');
    assert.equal(Theme.normalize('light'), 'light');
    assert.equal(Theme.normalize('dark'), 'dark');
    assert.deepEqual(Theme.PREFS, ['system', 'light', 'dark']);
});

test('an explicit choice wins; system follows the system setting', () => {
    assert.equal(Theme.resolve('light', true), 'light');
    assert.equal(Theme.resolve('dark', false), 'dark');
    assert.equal(Theme.resolve('system', true), 'dark');
    assert.equal(Theme.resolve('system', false), 'light');
    assert.equal(Theme.resolve(undefined, true), 'dark', 'no stored choice behaves like system');
});

// ---------------------------------------------------------------- the files

const themeCss = read('css', 'theme.css');
const stylesCss = read('css', 'styles.css');
const block = (selector) => {
    const i = themeCss.indexOf(selector + ' {');
    return themeCss.slice(i, themeCss.indexOf('\n}\n', i));
};
const vars = (text) => new Map([...text.matchAll(/--([a-z0-9-]+):\s*([^;]+);/g)].map(m => [m[1], m[2].trim()]));
const light = vars(block(':root'));
const dark = vars(block(':root[data-theme="dark"]'));

test('the dark theme defines exactly the variables the light theme defines', () => {
    const missing = [...light.keys()].filter(k => !dark.has(k));
    const extra = [...dark.keys()].filter(k => !light.has(k));
    assert.deepEqual(missing, [], 'defined for light, missing for dark');
    assert.deepEqual(extra, [], 'defined for dark, missing for light');
});

test('every variable the stylesheet uses is defined, and no colour is hard-coded in styles.css', () => {
    const own = vars(stylesCss.replace(/\/\*[\s\S]*?\*\//g, ''));
    const used = new Set([...stylesCss.matchAll(/var\(--([a-z0-9-]+)/g)].map(m => m[1]));
    const undefinedVars = [...used].filter(u => !light.has(u) && !own.has(u));
    assert.deepEqual(undefinedVars, []);
    const noComments = stylesCss.replace(/\/\*[\s\S]*?\*\//g, '');
    assert.deepEqual(noComments.match(/#[0-9a-fA-F]{3,8}\b/g) || [], [], 'a literal colour would look wrong in the other theme');
});

test('the graphs take their colours from the theme, not from literals', () => {
    for (const f of ['app.js', 'architecture.js']) assert.deepEqual(read('js', f).match(/#[0-9a-fA-F]{3,6}\b/g) || [], [], f);
    assert.match(read('js', 'app.js'), /document\.addEventListener\('themechange'/);
});

test('the page sets the theme before it is painted, with the same storage key as the script', () => {
    const html = read('index.html');
    const head = html.slice(0, html.indexOf('</head>'));
    assert.ok(head.indexOf("localStorage.getItem('av.theme')") > 0 && head.indexOf("localStorage.getItem('av.theme')") < head.indexOf('css/theme.css'), 'inline script runs before the stylesheets');
    assert.equal(Theme.KEY, 'av.theme');
    assert.match(head, /<meta name="color-scheme" content="light dark">/);
    const order = [...html.matchAll(/<script src="js\/([\w-]+\.js)"><\/script>/g)].map(m => m[1]);
    assert.ok(order.indexOf('theme.js') >= 0 && order.indexOf('theme.js') < order.indexOf('app.js'), 'theme.js loads before the code that draws graphs');
    assert.match(html, /<select id="themeSelect">[\s\S]*value="system"[\s\S]*value="light"[\s\S]*value="dark"/);
});

// ---------------------------------------------------------------- readability

const lum = (hex) => {
    const c = [0, 2, 4].map(i => parseInt(hex.slice(1 + i, 3 + i), 16) / 255).map(v => v <= .03928 ? v / 12.92 : Math.pow((v + .055) / 1.055, 2.4));
    return .2126 * c[0] + .7152 * c[1] + .0722 * c[2];
};
const ratio = (a, b) => { const [x, y] = [lum(a), lum(b)].sort((p, q) => q - p); return (x + .05) / (y + .05); };
const styleVars = vars(stylesCss.replace(/\/\*[\s\S]*?\*\//g, '').match(/:root \{[^}]*\}/)[0]);
function resolveIn(theme, name) {
    let value = theme.get(name) ?? styleVars.get(name);
    for (let i = 0; i < 6 && value && value.startsWith('var('); i++) { const n = value.match(/var\(--([a-z0-9-]+)\)/)[1]; value = theme.get(n) ?? styleVars.get(n); }
    return value;
}
for (const [name, theme] of [['light', light], ['dark', dark]]) {
    test(`${name}: text, muted text, links and code are readable on their surfaces (WCAG AA, 4.5:1)`, () => {
        const c = (n) => resolveIn(theme, n);
        const pairs = [
            ['text on page', c('text'), c('bg')], ['text on chrome', c('text'), c('chrome')], ['muted on page', c('muted'), c('bg')],
            ['accent on page', c('accent'), c('bg')], ['danger on page', c('danger'), c('bg')],
            ['code', c('code-fg'), c('code-bg')], ['keyword', c('code-kw'), c('code-bg')], ['string', c('code-string'), c('code-bg')],
            ['comment', c('code-comment'), c('code-bg')], ['number', c('code-number'), c('code-bg')], ['preprocessor', c('code-pre'), c('code-bg')],
            ['graph font', c('g-font'), c('g-cls-bg')], ['graph font (interface)', c('g-font'), c('g-if-bg')], ['graph font (method)', c('g-font'), c('g-ok-bg')],
            ['graph font (cycle)', c('g-font-bad'), c('g-bad-bg')], ['graph font (external)', c('g-font-ext'), c('g-ext-bg')], ['edge label', c('g-label'), c('g-canvas')]
        ];
        for (const t of ['success', 'error', 'info']) {                  // toast colours are set per type in styles.css
            const rule = stylesCss.match(new RegExp(String.raw`\.toast-${t} \{([^}]*)\}`))[1];
            const g = (k) => rule.match(new RegExp(String.raw`--toast-${k}: var\(--([a-z0-9-]+)\)`))[1];
            pairs.push(['toast ' + t, resolveIn(theme, g('fg')), resolveIn(theme, g('bg'))]);
        }
        const bad = pairs.filter(([, f, b]) => !/^#[0-9a-f]{6}$/i.test(f) || !/^#[0-9a-f]{6}$/i.test(b) || ratio(f, b) < 4.5).map(([n, f, b]) => `${n}: ${f} on ${b} = ${/^#/.test(f) && /^#/.test(b) ? ratio(f, b).toFixed(2) : 'unresolved'}`);
        assert.deepEqual(bad, []);
    });
}

test('the dark theme is dark and the light theme is light', () => {
    assert.ok(lum(resolveIn(dark, 'bg')) < .05 && lum(resolveIn(dark, 'text')) > .5);
    assert.ok(lum(resolveIn(light, 'bg')) > .8 && lum(resolveIn(light, 'text')) < .1);
    assert.ok(lum(resolveIn(dark, 'g-canvas')) < .05 && lum(resolveIn(light, 'g-canvas')) > .9);
});

test('the light theme keeps the colours the interface always had (the tokens are named for them)', () => {
    for (const [k, v] of light) { const m = k.match(/^(bg|fg|bd)-([0-9a-f]{6})$/); if (m) assert.equal(v.toLowerCase(), '#' + m[2], k); }
});

// ---------------------------------------------------------------- the browser part, with a fake window

function fakeWindow({ systemDark = false, stored = undefined, storageThrows = false } = {}) {
    const listeners = [];
    const mq = { matches: systemDark, addEventListener: (t, fn) => listeners.push(fn) };
    const events = [];
    const store = new Map(stored === undefined ? [] : [['av.theme', stored]]);
    const doc = { documentElement: { dataset: {} }, dispatchEvent: (e) => events.push(e.detail.resolved + ':' + e.detail.pref) };
    const win = {
        document: doc, matchMedia: () => mq, CustomEvent: class { constructor(type, init) { this.type = type; this.detail = init.detail; } },
        getComputedStyle: () => ({ getPropertyValue: (n) => (n === '--g-canvas' ? ' #1e1f22 ' : '#000000') }),
        localStorage: { getItem: (k) => { if (storageThrows) throw new Error('blocked'); return store.has(k) ? store.get(k) : null; }, setItem: (k, v) => { if (storageThrows) throw new Error('blocked'); store.set(k, v); } }
    };
    return { win, doc, mq, events, store, systemChanges: (dark) => { mq.matches = dark; listeners.forEach(fn => fn({ matches: dark })); } };
}

test('System follows the operating system live: when it switches, the page switches and tells the views', () => {
    const f = fakeWindow({ systemDark: false });
    const T = Theme.create(f.win);
    assert.equal(T.pref(), 'system');
    assert.equal(T.apply(), 'light');
    f.systemChanges(true);
    assert.equal(f.doc.documentElement.dataset.theme, 'dark');
    f.systemChanges(false);
    assert.equal(f.doc.documentElement.dataset.theme, 'light');
    assert.deepEqual(f.events, ['light:system', 'dark:system', 'light:system']);
});

test('an explicit Light or Dark choice ignores the operating system', () => {
    for (const choice of ['light', 'dark']) {
        const f = fakeWindow({ systemDark: false });
        const T = Theme.create(f.win);
        T.set(choice);
        f.systemChanges(true); f.systemChanges(false);
        assert.equal(f.doc.documentElement.dataset.theme, choice);
        assert.equal(f.store.get('av.theme'), choice, 'the choice is remembered');
    }
});

test('the stored choice is used on the next visit, and a damaged value falls back to system', () => {
    assert.equal(Theme.create(fakeWindow({ stored: 'dark' }).win).pref(), 'dark');
    assert.equal(Theme.create(fakeWindow({ stored: 'purple' }).win).pref(), 'system');
});

test('without usable storage the choice still applies to this page, and nothing throws', () => {
    const f = fakeWindow({ systemDark: false, storageThrows: true });
    const T = Theme.create(f.win);
    assert.equal(T.pref(), 'system');
    assert.equal(T.set('dark'), 'dark');
    assert.equal(f.doc.documentElement.dataset.theme, 'dark');
    f.systemChanges(false);
    assert.equal(f.doc.documentElement.dataset.theme, 'dark', 'the explicit choice stays while the page is open');
});

test('graph colours are read from the active theme and read again after a change', () => {
    const f = fakeWindow();
    const T = Theme.create(f.win);
    assert.equal(T.colors().canvas, '#1e1f22', 'values are trimmed');
    const first = T.colors();
    assert.strictEqual(T.colors(), first, 'cached while the theme stays the same');
    T.set('dark');
    assert.notStrictEqual(T.colors(), first, 'a theme change drops the cache');
});
