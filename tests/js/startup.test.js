// Structure checks on the page itself: script order, start-up, and the tab bar. They exist because of real bugs:
// the initial view became "metrics", app.js rendered it before metrics.js had loaded, and the page threw at start-up.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const root = path.join(__dirname, '..', '..', 'wwwroot');
const read = (...p) => fs.readFileSync(path.join(root, ...p), 'utf8').replace(/\r\n/g, '\n');

const RENDERERS = {
    inheritance: 'renderInheritance', calls: 'renderCalls', cycles: 'renderCycles', architecture: 'renderArchitecture', source: 'renderSource',
    compare: 'renderCompare', metrics: 'renderMetrics', risky: 'renderRisky', vulns: 'renderVulns'
};

test('the first render waits for all scripts: app.js never calls a view renderer while it is still loading', () => {
    const app = read('js', 'app.js');
    assert.match(app, /function startUp\(\) \{\n\s+buildExplorer\(\);\n\s+applyState\(\);/);
    assert.match(app, /DOMContentLoaded', startUp/);
    const topLevelCalls = app.split('\n').filter(l => /^(applyState|render[A-Z]\w*|buildExplorer)\(/.test(l));
    assert.deepEqual(topLevelCalls, []);
});

test('every view in the tab bar has a section in the page and a renderer defined by a script the page loads', () => {
    const html = read('index.html');
    const scripts = [...html.matchAll(/<script src="js\/([\w-]+\.js)"><\/script>/g)].map(m => m[1]);
    const sources = scripts.map(f => read('js', f)).join('\n');
    const views = [...html.matchAll(/data-view="(\w+)"/g)].map(m => m[1]);

    assert.deepEqual([...views].sort(), Object.keys(RENDERERS).sort(), 'the tab bar and this test must list the same views');
    for (const v of views) {
        assert.match(sources, new RegExp('function ' + RENDERERS[v] + '\\('), v + ': renderer defined');
        assert.ok(html.includes('id="view' + v[0].toUpperCase() + v.slice(1) + '"'), v + ': has a view section');
        assert.ok(html.includes('id="tab' + v[0].toUpperCase() + v.slice(1) + '"'), v + ': has a tab with the id the router expects');
    }
});

test('the tab bar follows the workflow: overview, then structure and relationships, then security and risk', () => {
    const html = read('index.html');
    const groups = [...html.matchAll(/<div class="tab-group" id="(\w+)">([\s\S]*?)<\/div>\s*<\/div>/g)]
        .map(m => ({ id: m[1], views: [...m[2].matchAll(/data-view="(\w+)"/g)].map(v => v[1]) }));
    assert.deepEqual(groups, [
        { id: 'groupOverview', views: ['metrics', 'architecture'] },
        { id: 'groupStructure', views: ['inheritance', 'calls', 'cycles', 'source'] },
        { id: 'groupSecurity', views: ['compare', 'risky', 'vulns'] }
    ]);
});

test('a newly opened assembly starts at the overview and selecting a class or method keeps its own rule', () => {
    const app = read('js', 'app.js');
    const resets = app.split('\n').filter(l => /^\s+state = \{ view: '/.test(l));
    assert.equal(resets.length, 2);                     // closing everything, and opening an assembly
    assert.ok(resets.every(l => l.includes("view: 'metrics'")), 'every place that resets the state starts at the overview');
    assert.match(app, /navigate\(\{ view: 'inheritance', type: ref\.id, method: null \}\)/);          // a class opens Inheritance
    assert.match(app, /navigate\(\{ view: 'calls', type: m\.parentClass, method: m\.id \}\)/);       // a method opens Method Calls
});

test('the obfuscation notice names what is verified and what is not, and can be dismissed', () => {
    const html = read('index.html');
    const notice = html.match(/<div id="obfuscationNotice">([\s\S]*?)<\/div>/)[1];
    assert.match(notice, /most accurate for unobfuscated assemblies/);
    assert.match(notice, /reflection/);
    assert.match(notice, /btnHideObfuscation/);
    assert.ok(!/not supported/i.test(notice), 'no blanket "not supported" claim');
});
