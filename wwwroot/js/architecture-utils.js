/*
 * Architecture analysis: dependencies between namespaces (and types) and the cycles among them.
 * Pure functions, no DOM; runs in the browser (window.ArchitectureUtils) and under node --test.
 */
(function (root, factory) {
    if (typeof module === 'object' && module.exports) module.exports = factory();
    else root.ArchitectureUtils = factory();
})(typeof self !== 'undefined' ? self : this, function () {
    'use strict';

    /**
     * Strongly connected components (Tarjan, iterative so a deep dependency chain cannot overflow the stack).
     * nodes: [id]; adjacency: Map(id -> [id]). Returns { components: [[id]], componentOf: Map(id -> index) }.
     * A component with more than one node (or a node that depends on itself) is a dependency cycle.
     */
    function stronglyConnected(nodes, adjacency) {
        let counter = 0;
        const index = new Map(), low = new Map(), onStack = new Set(), stack = [];
        const components = [], componentOf = new Map();

        for (const start of nodes) {
            if (index.has(start)) continue;
            const work = [{ v: start, i: 0 }];
            index.set(start, counter); low.set(start, counter); counter++;
            stack.push(start); onStack.add(start);

            while (work.length) {
                const frame = work[work.length - 1];
                const nbrs = adjacency.get(frame.v) || [];
                if (frame.i < nbrs.length) {
                    const w = nbrs[frame.i++];
                    if (!index.has(w)) {
                        index.set(w, counter); low.set(w, counter); counter++;
                        stack.push(w); onStack.add(w);
                        work.push({ v: w, i: 0 });
                    } else if (onStack.has(w)) {
                        low.set(frame.v, Math.min(low.get(frame.v), index.get(w)));
                    }
                } else {
                    if (low.get(frame.v) === index.get(frame.v)) {
                        const comp = [];
                        let w;
                        do { w = stack.pop(); onStack.delete(w); componentOf.set(w, components.length); comp.push(w); } while (w !== frame.v);
                        components.push(comp);
                    }
                    work.pop();
                    if (work.length) {
                        const parent = work[work.length - 1].v;
                        low.set(parent, Math.min(low.get(parent), low.get(frame.v)));
                    }
                }
            }
        }
        return { components, componentOf };
    }

    /**
     * Aggregates class-to-class dependencies into namespace-to-namespace ones.
     * classEdges: [{ from, to }] (class ids). namespaceOf: Map(classId -> namespace). Edges inside one namespace and edges to
     * classes outside the assembly are ignored. Returns { nodes: [{ id, types }], edges: [{ from, to, count, example }] }.
     */
    function namespaceGraph(classEdges, namespaceOf) {
        const typesIn = new Map();
        namespaceOf.forEach((ns) => typesIn.set(ns, (typesIn.get(ns) || 0) + 1));

        const agg = new Map();
        for (const e of classEdges) {
            const a = namespaceOf.get(e.from), b = namespaceOf.get(e.to);
            if (a === undefined || b === undefined || a === b) continue;
            const key = a + '\u0001' + b;
            const cur = agg.get(key);
            if (cur) cur.count++; else agg.set(key, { from: a, to: b, count: 1, example: e.from + ' → ' + e.to });
        }
        return {
            nodes: [...typesIn.keys()].sort().map(id => ({ id, types: typesIn.get(id) })),
            edges: [...agg.values()].sort((x, y) => y.count - x.count)
        };
    }

    function adjacencyOf(edges) {
        const adj = new Map();
        for (const e of edges) { if (!adj.has(e.from)) adj.set(e.from, []); adj.get(e.from).push(e.to); }
        return adj;
    }

    /** The cyclic components of a graph (size > 1, or a self-dependency), biggest first, each with one concrete cycle path. */
    function findCycles(nodeIds, edges) {
        const adj = adjacencyOf(edges);
        const { components } = stronglyConnected(nodeIds, adj);
        const selfLoops = new Set(edges.filter(e => e.from === e.to).map(e => e.from));
        return components
            .filter(c => c.length > 1 || selfLoops.has(c[0]))
            .map(c => ({ members: c.slice().sort(), path: cyclePath(c, adj) }))
            .sort((a, b) => b.members.length - a.members.length || a.members[0].localeCompare(b.members[0]));
    }

    /** One concrete loop A -> B -> C -> A inside a component (shortest loop through its first member). */
    function cyclePath(component, adjacency) {
        const inside = new Set(component);
        const start = component.slice().sort()[0];
        const prev = new Map([[start, null]]);
        const queue = [start];
        while (queue.length) {
            const v = queue.shift();
            for (const w of (adjacency.get(v) || [])) {
                if (!inside.has(w)) continue;
                if (w === start) {
                    const path = [v];
                    for (let p = prev.get(v); p !== null && p !== undefined; p = prev.get(p)) path.unshift(p);
                    return [...path, start];
                }
                if (!prev.has(w)) { prev.set(w, v); queue.push(w); }
            }
        }
        return [start, start];
    }

    /**
     * Layers for drawing: callers on the left, what they depend on to the right (a namespace is always left of the ones it
     * depends on). Members of one cycle share a layer.
     * Returns Map(id -> layer >= 0).
     */
    function layers(nodeIds, edges) {
        const adj = adjacencyOf(edges);
        const { components, componentOf } = stronglyConnected(nodeIds, adj);
        const dag = components.map(() => new Set());
        edges.forEach(e => {
            const a = componentOf.get(e.from), b = componentOf.get(e.to);
            if (a !== undefined && b !== undefined && a !== b) dag[a].add(b);
        });

        // longest path from a component to a leaf of the condensed DAG, computed iteratively with memoisation
        const depth = new Array(components.length).fill(-1);
        for (let s = 0; s < components.length; s++) {
            if (depth[s] >= 0) continue;
            const work = [s];
            while (work.length) {
                const c = work[work.length - 1];
                const pending = [...dag[c]].filter(d => depth[d] < 0);
                if (pending.length) { work.push(...pending); continue; }
                depth[c] = [...dag[c]].reduce((m, d) => Math.max(m, depth[d] + 1), 0);
                work.pop();
            }
        }
        // callers on the left: layer = (max depth) - depth
        const max = depth.reduce((m, d) => Math.max(m, d), 0);
        const result = new Map();
        nodeIds.forEach(id => result.set(id, max - depth[componentOf.get(id)]));
        return result;
    }

    return { stronglyConnected, namespaceGraph, findCycles, cyclePath, layers };
});
