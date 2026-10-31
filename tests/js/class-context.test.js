// The selected class / method is the context of the views: which view stays open when another one is picked, and which circular chains
// belong to the selection. Uses the real analyzer output of the circular-dependency sample.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const Model = require('../../wwwroot/js/analysis-model.js');

const m = Model.build(JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures', 'CircularDependencies.analysis.json'), 'utf8')));
const ctx = { cycles: m.cycles, methodsByClass: m.methodsByClass };
const chains = (sel) => Model.cycleChains({ type: null, method: null, cycleAll: false, ...sel }, ctx);
const idOf = (cls, name) => m.methodsByClass.get(cls).find(x => x.name.startsWith(name + '(')).id;

test('picking a class keeps the Code Navigator or Cycles open; every other view opens the Code Navigator', () => {
    for (const v of ['navigator', 'cycles']) assert.equal(Model.viewAfterSelect(v, 'type'), v);
    for (const v of ['overview', 'metrics', 'architecture', 'risky', 'vulns', 'compare']) assert.equal(Model.viewAfterSelect(v, 'type'), 'navigator');
});

test('picking a method keeps the Code Navigator or Cycles open; the summaries open the Code Navigator', () => {
    for (const v of ['navigator', 'cycles']) assert.equal(Model.viewAfterSelect(v, 'method'), v);
    for (const v of ['overview', 'metrics', 'architecture']) assert.equal(Model.viewAfterSelect(v, 'method'), 'navigator');
});

test('nothing selected, or "show all": every chain, unchanged', () => {
    assert.equal(chains({}).kind, 'all');
    assert.equal(chains({}).chains.length, m.cycles.length);
    assert.equal(chains({ type: 'Samples.Cycles.Left', cycleAll: true }).chains.length, m.cycles.length);
});

test('a selected method: only the chains through it, each starting at that method', () => {
    const a = idOf('Samples.Cycles.SimpleCycle', 'B');
    const r = chains({ type: 'Samples.Cycles.SimpleCycle', method: a });
    assert.equal(r.kind, 'method');
    assert.ok(r.chains.length >= 1);
    for (const c of r.chains) { assert.equal(c.body[0], a); assert.ok(m.cycles[c.index].body.includes(a)); assert.equal(c.body.length, m.cycles[c.index].body.length); }
});

test('a selected class: the chains through any of its methods (a simple A -> B -> C -> A cycle and a larger one)', () => {
    const left = chains({ type: 'Samples.Cycles.Left' });
    assert.equal(left.kind, 'class');
    assert.ok(left.chains.length >= 1);
    const own = new Set(m.methodsByClass.get('Samples.Cycles.Left').map(x => x.id));
    for (const c of left.chains) assert.ok(own.has(c.body[0]), 'the chain starts at a method of the selected class');
    assert.equal(chains({ type: 'Samples.Cycles.SimpleCycle' }).chains.length, 1);
});

test('a class whose methods are in no cycle has no chains (no false report)', () => {
    const clean = [...m.methodsByClass.keys()].filter(t => !m.methodsByClass.get(t).some(x => m.cyclesOfMethod.has(x.id)));
    for (const t of clean) assert.equal(chains({ type: t }).chains.length, 0, t);
    const inCycle = [...m.methodsByClass.keys()].filter(t => m.methodsByClass.get(t).some(x => m.cyclesOfMethod.has(x.id)));
    for (const t of inCycle) assert.ok(chains({ type: t }).chains.length > 0, t);
});

test('a method that is in two chains shows both; the chain index stays the one used everywhere else', () => {
    const twoCycles = [...m.cyclesOfMethod.entries()].find(([, list]) => list.length > 1);
    if (!twoCycles) return;
    const r = chains({ method: twoCycles[0] });
    assert.deepEqual(r.chains.map(c => c.index).sort(), [...twoCycles[1]].sort());
});

const root = path.join(__dirname, '..', '..', 'wwwroot');
const read = (...p) => fs.readFileSync(path.join(root, ...p), 'utf8').replace(/\r\n/g, '\n');

test('wiring: every explorer / graph selection goes through selectClass / selectMethod, and the cycle filter has no state of its own', () => {
    const app = read('js', 'app.js');
    assert.ok(!/cycleMethod/.test(app), 'the cycle filter follows the selection (type / method) instead of a second, separate one');
    assert.match(app, /function selectClass\(typeId\)[\s\S]*?viewAfterSelect\(state\.view, 'type'\)/);
    assert.match(app, /function selectMethod\(methodId\)[\s\S]*?viewAfterSelect\(state\.view, 'method'\)/);
    assert.match(app, /onSelect: \(\) => selectMethod\(m\.id\)/);
    assert.match(app, /onSelect: \(\) => selectClass\(typeId\)/);
    assert.match(app, /'type' in patch \|\| 'method' in patch/, 'a new selection ends a "show all chains" choice');
});

test('wiring: the selection bar opens the same selection in the other views, and a method keeps its class', () => {
    const app = read('js', 'app.js');
    for (const v of ['navigator', 'cycles']) assert.match(app, new RegExp("\\['" + v + "', '"));
    assert.match(read('js', 'source.js'), /sourceScopeFor/, 'the "whole type" choice does not leak to the next method');
});

test('the Inheritance view carries the C# code of the selected class, using the shared code renderer', () => {
    const html = read('index.html');
    assert.ok(html.includes('id="inhCodeScroll"') && html.includes('id="btnInhCode"'));
    const app = read('js', 'app.js');
    assert.match(app, /renderSource\(byId\('inhCodeScroll'\), 'type'\)/);
    assert.match(read('js', 'source.js'), /function renderSource\(root, mode\)/);
    assert.match(app, /function renderInheritance\(\)[\s\S]*?renderInheritanceCode\(\);\n\}/, 'every redraw of the view refreshes the code too');
});

test('the Method Calls view carries the C# code of the selected method (the class while only a class is selected)', () => {
    const html = read('index.html'), app = read('js', 'app.js');
    assert.ok(html.includes('id="callCodeScroll"') && html.includes('id="btnCallCode"') && !html.includes('btnSourceCalls'));
    assert.match(app, /renderSource\(byId\('callCodeScroll'\), state\.method \? 'method' : 'type'\)/);
    assert.match(app, /function renderCalls\(\) \{\n    renderCallsGraph\(\);\n    renderCallsCode\(\);/);
});

test('one switch hides or shows the code panel in both views', () => {
    const app = read('js', 'app.js');
    assert.match(app, /byId\('btnInhCode'\)\.onclick = toggleCodePanel/);
    assert.match(app, /byId\('btnCallCode'\)\.onclick = toggleCodePanel/);
});

test('the three former views are one Code Navigator tab: a method shows calls, a class shows inheritance, both with code', () => {
    const V = require('../../wwwroot/js/views.js');
    const html = read('index.html'), app = read('js', 'app.js');
    assert.deepEqual(V.inGroup('groupDetail').map(v => v.id), ['navigator', 'cycles']);
    assert.deepEqual(V.sections('navigator'), ['viewInheritance', 'viewCalls']);
    for (const old of ['tabInheritance', 'tabCalls', 'tabSource', 'viewSource', 'sourceScroll']) assert.ok(!html.includes(old), old + ' is gone');
    assert.match(app, /function navMode\(\) \{ return state\.method \? 'calls' : 'inheritance'; \}/);
    assert.ok(!/view: '(inheritance|calls|source)'/.test(app + read('js', 'metrics.js') + read('js', 'risky.js') + read('js', 'architecture.js')), 'every link opens the navigator');
});

test('only a failed request is reported as a network problem; a script error keeps its own message', () => {
    const app = read('js', 'app.js');
    assert.ok(!/instanceof TypeError/.test(app), 'a TypeError thrown while drawing a view is not "the server did not answer"');
    assert.match(app, /Object\.assign\(new Error\('network error'\), \{ network: true \}\)/);
    assert.match(app, /const reason = err && err\.network/);
});
