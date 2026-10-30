// Integration tests for the browser-side model: the REAL analyzer output for the sample assemblies (tests/js/fixtures, regenerated
// with scripts/update-js-fixtures) goes through the same model code the views use, together with the architecture and depth logic.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const Model = require('../../wwwroot/js/analysis-model.js');
const Arch = require('../../wwwroot/js/architecture-utils.js');
const G = require('../../wwwroot/js/graph-utils.js');

const load = (name) => JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures', name + '.analysis.json'), 'utf8'));
const circular = Model.build(load('CircularDependencies'));
const calls = Model.build(load('MethodCalls'));
const inheritance = Model.build(load('Inheritance'));

const idOf = (m, prefix) => [...m.methodById.keys()].find(id => id.startsWith(prefix + '('));

// ------------------------------------------------------------------------------------------ circular dependencies -> Method View

test('a method that is part of a cycle gets the warning and the link target the Method View shows', () => {
    const a = idOf(circular, 'Samples.Cycles.SimpleCycle.A');
    const info = Model.cycleInfo(circular, a);
    assert.ok(info, 'SimpleCycle.A is in a cycle');
    assert.equal(info.count, 1);
    const cycle = Model.describeCycle(circular, info.first);
    assert.equal(cycle.length, 3);
    assert.deepEqual(cycle.methods.map(id => id.split('(')[0]).sort(), ['Samples.Cycles.SimpleCycle.A', 'Samples.Cycles.SimpleCycle.B', 'Samples.Cycles.SimpleCycle.C']);
    assert.deepEqual(cycle.classes, ['Samples.Cycles.SimpleCycle']);
});

test('the entry method of a cycle and plain chains show no warning', () => {
    assert.equal(Model.cycleInfo(circular, idOf(circular, 'Samples.Cycles.EntryOutside.Start')), null);
    assert.equal(Model.cycleInfo(circular, idOf(circular, 'Samples.Cycles.Acyclic.Top')), null);
    assert.equal(Model.cycleInfo(circular, idOf(circular, 'Samples.Cycles.Recursion.Factorial')), null);
});

test('every cycle appears once even though the analyzer reports it from several starting points', () => {
    assert.equal(circular.cycles.length, 7);
    assert.equal(new Set(circular.cycles.map(c => Model.canonicalKey(c.body))).size, 7);
});

test('a cycle across three classes lists all three classes, so each can navigate to it', () => {
    const left = Model.cycleInfo(circular, idOf(circular, 'Samples.Cycles.Left.Step'));
    const cycle = Model.describeCycle(circular, left.first);
    assert.deepEqual(cycle.classes.sort(), ['Samples.Cycles.Left', 'Samples.Cycles.Middle', 'Samples.Cycles.Right']);
    for (const cls of ['Left', 'Middle', 'Right'])
        assert.ok(Model.cycleInfo(circular, idOf(circular, `Samples.Cycles.${cls}.Step`)), cls + ' links to the same cycle');
});

test('cyclic edges are marked so the graphs can draw them red', () => {
    const a = idOf(circular, 'Samples.Cycles.SimpleCycle.A'), b = idOf(circular, 'Samples.Cycles.SimpleCycle.B');
    assert.ok(circular.cyclicEdges.has(a + Model.SEP + b));
});

// ------------------------------------------------------------------------------------------ navigation rules

test('selecting a class opens Inheritance and selecting a method opens Method Calls', () => {
    assert.equal(Model.viewForSelection('type'), 'inheritance');
    assert.equal(Model.viewForSelection('class'), 'inheritance');
    assert.equal(Model.viewForSelection('method'), 'calls');
});

// ------------------------------------------------------------------------------------------ Architecture from real data

test('the two namespaces that use each other form a namespace cycle in the Architecture analysis', () => {
    const classEdges = [];
    load('CircularDependencies').methodGraph.edges.forEach(e => { if (circular.nsOf.has(e.fromClass) && circular.nsOf.has(e.toClass)) classEdges.push({ from: e.fromClass, to: e.toClass }); });
    const g = Arch.namespaceGraph(classEdges, circular.nsOf);
    const cycles = Arch.findCycles(g.nodes.map(n => n.id), g.edges);
    const billing = cycles.find(c => c.members.includes('Samples.Cycles.Billing') && c.members.includes('Samples.Cycles.Shipping'));
    assert.ok(billing, 'Billing <-> Shipping must be reported');
    assert.equal(billing.members.length, 2);
});

test('the main namespace is not in a cycle with Billing and Shipping', () => {
    const classEdges = load('CircularDependencies').methodGraph.edges.map(e => ({ from: e.fromClass, to: e.toClass }));
    const g = Arch.namespaceGraph(classEdges, circular.nsOf);
    const cycles = Arch.findCycles(g.nodes.map(n => n.id), g.edges);
    assert.ok(cycles.every(c => !c.members.includes('Samples.Cycles') || c.members.length === 1));
});

// ------------------------------------------------------------------------------------------ depth model from real data

test('the 4-level call chain gives depth choices 1, 2, 3 and 4 - no level is missing', () => {
    const run = idOf(calls, 'Samples.MethodCalls.Pipeline.Run');
    const reach = G.reachableDepth([run], (id) => calls.calleesOf.get(id));
    assert.ok(reach >= 3);
    const { levels } = G.bfsLevels([run], (id) => calls.calleesOf.get(id), 10);
    const depthOf = (m) => levels.get(idOf(calls, m));
    assert.equal(depthOf('Samples.MethodCalls.Pipeline.Run'), 0);
    assert.equal(depthOf('Samples.MethodCalls.Pipeline.Load'), 1);
    assert.equal(depthOf('Samples.MethodCalls.Pipeline.Parse'), 2);
    assert.equal(depthOf('Samples.MethodCalls.Pipeline.Validate'), 3);
});

test('RoundedSquare has four class ancestors plus the interfaces above them (depth 5), and the generic base is kept', () => {
    const rounded = 'Samples.Inheritance.RoundedSquare';
    const ids = (id) => (inheritance.basesOf.get(id) || []).map(b => b.id);
    assert.equal(G.reachableDepth([rounded], ids), 5);                       // Square, Rectangle, Polygon, Shape, then IShape / INamed
    assert.deepEqual(G.depthChoices(G.reachableDepth([rounded], ids)), [1, 2, 3, 4, 5]);          // depth 4 is a real choice (the old fixed list skipped it)
    assert.deepEqual(ids('Samples.Inheritance.UserRepository'), ['Samples.Inheritance.Repository`1']);
});

test('nested types stay in the real namespace and are shown as Outer.Inner.Deepest (no invented namespaces)', () => {
    assert.deepEqual([...inheritance.typesByNs.keys()], ['Samples.Inheritance']);
    assert.equal(inheritance.nsOf.get('Samples.Inheritance.Outer.Inner.Deepest'), 'Samples.Inheritance');
    assert.equal(inheritance.typeNodes.get('Samples.Inheritance.Outer.Inner.Deepest').name, 'Outer.Inner.Deepest');
    assert.equal(inheritance.typeNodes.get('Samples.Inheritance.Shape').name, 'Shape');
});

test('older analyses without a namespace field still build (best effort from the name)', () => {
    const m = Model.build({ classes: ['A.B.C'], inheritance: { nodes: [{ id: 'A.B.C', name: 'C', type: 'Class' }], edges: [] } });
    assert.equal(m.nsOf.get('A.B.C'), 'A.B');
});

test('derived types are found from the base type', () => {
    const derived = (inheritance.derivedOf.get('Samples.Inheritance.Shape') || []).map(d => d.id).sort();
    assert.ok(derived.includes('Samples.Inheritance.Circle') && derived.includes('Samples.Inheritance.Polygon'));
});

// ------------------------------------------------------------------------------------------ robustness

test('an empty or partial analysis builds an empty model instead of throwing', () => {
    for (const data of [undefined, null, {}, { classes: [] }, { methodGraph: { nodes: [], edges: [] } }, { deadlocks: [[]] }]) {
        const m = Model.build(data);
        assert.equal(m.cycles.length, 0);
        assert.equal(m.typeNodes.size, 0);
    }
});

test('a cycle that is written closed (first method repeated at the end) is stored once without the repeat', () => {
    const m = Model.build({ deadlocks: [['a', 'b', 'c', 'a'], ['b', 'c', 'a']], methodGraph: { nodes: [], edges: [] } });
    assert.equal(m.cycles.length, 1);
    assert.deepEqual(m.cycles[0].body, ['a', 'b', 'c']);
});

test('duplicate call edges collapse into one edge with a count', () => {
    const m = Model.build({ methodGraph: { nodes: [], edges: [{ from: 'a', to: 'b' }, { from: 'a', to: 'b' }, { from: 'a', to: 'c' }] } });
    assert.deepEqual(m.calleesOf.get('a'), ['b', 'c']);
    assert.equal(m.edgeCounts.get('a' + Model.SEP + 'b'), 2);
    assert.deepEqual(m.callersOf.get('b'), ['a']);
});

// ------------------------------------------------------------------------------------------ diagnostics: nothing is silently dropped

test('a clean analysis has nothing to say', () => {
    assert.equal(Model.describeDiagnostics({ complete: true, skippedTypes: 0, skippedMethods: 0, skippedInterfaces: 0, unreadableBodies: 0, referenceAssembly: false }), '');
    assert.equal(Model.describeDiagnostics(undefined), '');
});

test('skipped items are counted in plain words and the result is called incomplete', () => {
    const text = Model.describeDiagnostics({ skippedTypes: 1, skippedMethods: 3, skippedInterfaces: 0, unreadableBodies: 2 });
    assert.match(text, /1 type, 3 methods, the code of 2 methods/);
    assert.match(text, /results are incomplete/);
});

test('a reference assembly is explained as having no real code', () => {
    const text = Model.describeDiagnostics({ referenceAssembly: true });
    assert.match(text, /reference assembly/);
    assert.match(text, /no real code/);
});

test('obfuscation: a clean assembly says nothing on load, an obfuscated one gives one sentence with its evidence', () => {
    assert.equal(Model.describeObfuscation(undefined), '');
    assert.equal(Model.describeObfuscation({ likely: false, evidence: [] }), '');
    const text = Model.describeObfuscation({ likely: true, evidence: ['72% of its names (36 of 50) are one or two letters'] });
    assert.match(text, /signs of obfuscation/);
    assert.match(text, /72% of its names/);
    assert.match(text, /may be incomplete/);
});
