const test = require('node:test');
const assert = require('node:assert/strict');
const A = require('../../wwwroot/js/architecture-utils.js');

const edges = (...pairs) => pairs.map(([from, to]) => ({ from, to }));

test('a chain has no cycles', () => {
    assert.deepEqual(A.findCycles(['a', 'b', 'c'], edges(['a', 'b'], ['b', 'c'])), []);
});

test('a loop is found with one concrete path that closes on itself', () => {
    const [c] = A.findCycles(['a', 'b', 'c', 'd'], edges(['a', 'b'], ['b', 'c'], ['c', 'a'], ['c', 'd']));
    assert.deepEqual(c.members, ['a', 'b', 'c']);
    assert.deepEqual(c.path, ['a', 'b', 'c', 'a']);
});

test('two separate cycles and a self-dependency are all reported, biggest first', () => {
    const cycles = A.findCycles(['a', 'b', 'c', 'd', 'e', 'f', 'g'], edges(['a', 'b'], ['b', 'a'], ['c', 'd'], ['d', 'e'], ['e', 'c'], ['f', 'f'], ['g', 'a']));
    assert.deepEqual(cycles.map(c => c.members.length), [3, 2, 1]);
    assert.deepEqual(cycles[2].members, ['f']);
});

test('overlapping cycles form one component', () => {
    const [c] = A.findCycles(['a', 'b', 'c'], edges(['a', 'b'], ['b', 'a'], ['b', 'c'], ['c', 'b']));
    assert.deepEqual(c.members, ['a', 'b', 'c']);
});

test('a very long chain does not overflow the stack and a long ring is one cycle', () => {
    const n = 50000;
    const ids = Array.from({ length: n }, (_, i) => 'n' + i);
    const chain = ids.slice(1).map((id, i) => ({ from: ids[i], to: id }));
    assert.deepEqual(A.findCycles(ids, chain), []);
    const ring = [...chain, { from: ids[n - 1], to: ids[0] }];
    const [c] = A.findCycles(ids, ring);
    assert.equal(c.members.length, n);
});

test('class dependencies become namespace dependencies; same-namespace and unknown classes are ignored', () => {
    const ns = new Map([['A.X', 'A'], ['A.Y', 'A'], ['B.Z', 'B'], ['C.W', 'C']]);
    const g = A.namespaceGraph([
        { from: 'A.X', to: 'B.Z' }, { from: 'A.Y', to: 'B.Z' }, { from: 'B.Z', to: 'C.W' },
        { from: 'A.X', to: 'A.Y' }, { from: 'A.X', to: 'System.Object' }], ns);
    assert.deepEqual(g.nodes.map(n => [n.id, n.types]), [['A', 2], ['B', 1], ['C', 1]]);
    assert.deepEqual(g.edges.map(e => [e.from, e.to, e.count]), [['A', 'B', 2], ['B', 'C', 1]]);
    assert.equal(g.edges[0].example, 'A.X → B.Z');
});

test('layers put what depends on others to the left and cycle members in the same layer', () => {
    const ids = ['ui', 'core', 'util', 'db', 'cache'];
    const l = A.layers(ids, edges(['ui', 'core'], ['core', 'util'], ['core', 'db'], ['db', 'cache'], ['cache', 'db']));
    assert.ok(l.get('ui') < l.get('core') && l.get('core') < l.get('util'));
    assert.equal(l.get('db'), l.get('cache'));
    assert.ok(l.get('core') < l.get('db'));
});

test('empty and single-node graphs are fine', () => {
    assert.deepEqual(A.findCycles([], []), []);
    assert.equal(A.layers(['x'], []).get('x'), 0);
    assert.deepEqual(A.namespaceGraph([], new Map()).edges, []);
});
