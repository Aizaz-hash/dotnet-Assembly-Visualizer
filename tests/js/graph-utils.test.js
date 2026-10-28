const test = require('node:test');
const assert = require('node:assert/strict');
const G = require('../../wwwroot/js/graph-utils.js');

const adj = (obj) => (id) => obj[id] || [];

test('depth is the shortest-path distance, starting at 0', () => {
    const { levels, reached } = G.bfsLevels(['A'], adj({ A: ['B', 'C'], B: ['D'], C: ['D'], D: ['E'], E: ['F'] }), 10);
    assert.deepEqual([...levels], [['A', 0], ['B', 1], ['C', 1], ['D', 2], ['E', 3], ['F', 4]]);
    assert.equal(reached, 4);
});

test('no depth level is skipped: a chain of 6 offers depths 1..6 including 4', () => {
    const chain = { A: ['B'], B: ['C'], C: ['D'], D: ['E'], E: ['F'], F: ['G'] };
    const max = G.reachableDepth(['A'], adj(chain));
    assert.equal(max, 6);
    assert.deepEqual(G.depthChoices(max), [1, 2, 3, 4, 5, 6]);
});

test('duplicates and merged paths do not change a depth', () => {
    const { levels } = G.bfsLevels(['A'], adj({ A: ['B', 'B', 'C'], B: ['D'], C: ['D', 'D'], D: [] }), 5);
    assert.equal(levels.get('D'), 2);
    assert.equal(levels.size, 4);
});

test('cycles terminate and keep the first (shortest) depth', () => {
    const { levels } = G.bfsLevels(['A'], adj({ A: ['B'], B: ['C'], C: ['A', 'B'] }), 50);
    assert.deepEqual([...levels], [['A', 0], ['B', 1], ['C', 2]]);
    assert.equal(G.reachableDepth(['A'], adj({ A: ['B'], B: ['A'] })), 1);
});

test('maxDepth and maxNodes are honoured and truncation reported', () => {
    const wide = { A: ['B', 'C', 'D', 'E'] };
    const r = G.bfsLevels(['A'], adj(wide), 1, { maxNodes: 3 });
    assert.equal(r.levels.size, 3);
    assert.equal(r.truncated, true);
    const shallow = G.bfsLevels(['A'], adj({ A: ['B'], B: ['C'] }), 1);
    assert.equal(shallow.levels.has('C'), false);
});

test('callers and callees share one levels map with signed depths', () => {
    const levels = new Map([['M', 0]]);
    G.bfsLevels(['M'], adj({ M: ['x'], x: ['y'] }), 3, { levels, sign: 1 });
    G.bfsLevels(['M'], adj({ M: ['c'], c: ['d'] }), 3, { levels, sign: -1 });
    assert.equal(levels.get('y'), 2);
    assert.equal(levels.get('d'), -2);
});

test('an unreachable-depth request on a leaf offers depth 1 only', () => {
    assert.deepEqual(G.depthChoices(G.reachableDepth(['A'], adj({ A: [] }))), [1]);
});

function makeGraph(levelsSpec, edgeFn) {
    const nodes = [], edges = [];
    levelsSpec.forEach((count, level) => {
        for (let i = 0; i < count; i++) nodes.push({ id: `L${level}N${i}`, level, label: `Namespace.Class.Method${i}(int a, string b)` });
    });
    edgeFn(nodes, edges);
    return { nodes, edges };
}

function assertNoOverlap(res) {
    const boxes = [...res.positions.values()];
    for (let i = 0; i < boxes.length; i++)
        for (let j = i + 1; j < boxes.length; j++)
            assert.equal(G.boxesOverlap(boxes[i], boxes[j]), false, `boxes ${i} and ${j} overlap`);
}

test('layout never overlaps nodes: wide, deep and branching graphs', () => {
    for (const spec of [[1, 5, 12, 30], [1, 1, 1, 1, 1, 1, 1, 1], [1, 3, 9, 27, 81], [1, 100]]) {
        const { nodes, edges } = makeGraph(spec, (ns, es) => {
            ns.forEach(n => { if (n.level > 0) { const parents = ns.filter(p => p.level === n.level - 1); es.push({ from: parents[(parseInt(n.id.split('N')[1]) * 7) % parents.length].id, to: n.id }); } });
        });
        assertNoOverlap(G.layoutLayered(nodes, edges, { direction: 'LR' }));
        assertNoOverlap(G.layoutLayered(nodes, edges, { direction: 'UD' }));
    }
});

test('spacing adapts: a sparse chain is much more compact than a dense graph', () => {
    const chain = makeGraph([1, 1, 1, 1], (ns, es) => ns.slice(1).forEach((n, i) => es.push({ from: ns[i].id, to: n.id })));
    const dense = makeGraph([1, 8, 8, 8], (ns, es) => {
        ns.forEach(a => ns.forEach(b => { if (b.level === a.level + 1) es.push({ from: a.id, to: b.id }); }));
    });
    const c = G.layoutLayered(chain.nodes, chain.edges), d = G.layoutLayered(dense.nodes, dense.edges);
    const chainGap = Math.min(...c.levelGap.values()), denseGap = Math.min(...d.levelGap.values());
    assert.ok(chainGap < denseGap, `chain gap ${chainGap} should be below dense gap ${denseGap}`);
    assert.ok(chainGap <= 90, 'a plain chain should not get a huge fixed spacing');
});

test('levels run in order along the flow axis, callers before the root before callees', () => {
    const nodes = [{ id: 'c', level: -1 }, { id: 'm', level: 0 }, { id: 'x', level: 1 }, { id: 'y', level: 2 }];
    const edges = [{ from: 'c', to: 'm' }, { from: 'm', to: 'x' }, { from: 'x', to: 'y' }];
    const p = G.layoutLayered(nodes, edges, { direction: 'LR' }).positions;
    assert.ok(p.get('c').x < p.get('m').x && p.get('m').x < p.get('x').x && p.get('x').x < p.get('y').x);
});

test('layout copes with an empty graph, one node, self-loops, cycles and unknown edge ends', () => {
    assert.equal(G.layoutLayered([], []).positions.size, 0);
    assert.equal(G.layoutLayered([{ id: 'a', level: 0 }], []).positions.size, 1);
    const r = G.layoutLayered([{ id: 'a', level: 0 }, { id: 'b', level: 1 }], [{ from: 'a', to: 'a' }, { from: 'a', to: 'b' }, { from: 'b', to: 'a' }, { from: 'a', to: 'zzz' }]);
    assert.equal(r.positions.size, 2);
    assertNoOverlap(r);
});
