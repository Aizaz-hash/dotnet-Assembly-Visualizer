/*
 * Graph helpers shared by the Inheritance view and the Method (call) view.
 *
 *  - bfsLevels / reachableDepth: ONE definition of "depth". Depth 0 is the starting node and a node's depth is
 *    the length of the shortest path to it, so duplicates, merged paths and cycles never change a depth and
 *    every node at the same logical distance gets the same number.
 *  - layoutLayered: a layered layout whose spacing follows the graph (node sizes, nodes per level, edges between
 *    levels, node degree) instead of one fixed gap, so small graphs stay compact and dense ones stay readable.
 *
 * Plain functions, no DOM access: usable from the browser (window.GraphUtils) and from node --test.
 */
(function (root, factory) {
    if (typeof module === 'object' && module.exports) module.exports = factory();
    else root.GraphUtils = factory();
})(typeof self !== 'undefined' ? self : this, function () {
    'use strict';

    // ---------------------------------------------------------------- depth model

    /**
     * Breadth-first traversal. neighbors(id) -> iterable of ids.
     * Returns { levels: Map(id -> depth), truncated, reached }.
     * `levels` already contains the roots at depth 0 (when they are given as ids). opts.maxNodes caps the size.
     */
    function bfsLevels(rootIds, neighbors, maxDepth, opts) {
        const o = opts || {};
        const sign = o.sign || 1;
        const levels = o.levels || new Map();
        const maxNodes = o.maxNodes || Infinity;
        const roots = Array.isArray(rootIds) ? rootIds : [rootIds];
        let truncated = false, reached = 0;
        let frontier = [];
        for (const r of roots) {
            if (!levels.has(r)) { levels.set(r, 0); }
            frontier.push(r);
        }
        for (let d = 1; d <= maxDepth && frontier.length; d++) {
            const next = [];
            for (const id of frontier) {
                for (const n of (neighbors(id) || [])) {
                    if (levels.has(n)) continue;            // a node is placed once, at its shortest distance
                    if (levels.size >= maxNodes) { truncated = true; continue; }
                    levels.set(n, sign * d);
                    next.push(n);
                    reached = d;
                }
            }
            frontier = next;
        }
        return { levels, truncated, reached };
    }

    /** How many levels exist below `rootIds` (longest shortest-path), capped at `cap`. */
    function reachableDepth(rootIds, neighbors, cap) {
        const seen = new Set(Array.isArray(rootIds) ? rootIds : [rootIds]);
        let frontier = [...seen], depth = 0;
        const limit = cap || 1000;
        while (frontier.length && depth < limit) {
            const next = [];
            for (const id of frontier) {
                for (const n of (neighbors(id) || [])) {
                    if (seen.has(n)) continue;
                    seen.add(n);
                    next.push(n);
                }
            }
            if (!next.length) break;
            depth++;
            frontier = next;
        }
        return depth;
    }

    /** [1 .. max] - the depth choices a view should offer; always at least [1]. */
    function depthChoices(maxDepth) {
        const n = Math.max(1, maxDepth | 0);
        return Array.from({ length: n }, (_, i) => i + 1);
    }

    // ---------------------------------------------------------------- layout

    const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));

    /** Estimated box of a node from its label (the caller can override with nodeSize). */
    function estimateSize(label, o) {
        const charW = o.charWidth, maxW = o.maxNodeWidth;
        const text = String(label == null ? '' : label);
        const w = clamp(text.length * charW + o.nodePadding * 2, o.minNodeWidth, maxW);
        const lines = Math.max(1, Math.ceil((text.length * charW) / (maxW - o.nodePadding * 2)));
        return { w, h: lines * o.lineHeight + o.nodePadding * 2 };
    }

    /**
     * nodes: [{ id, level, label }]   (level = integer; negative levels sit before level 0)
     * edges: [{ from, to }]
     * options.direction: 'LR' (levels left->right, default) or 'UD' (levels top->bottom)
     * Returns { positions: Map(id -> {x, y, w, h}), width, height, levelGap: Map }
     */
    function layoutLayered(nodes, edges, options) {
        const o = Object.assign({
            direction: 'LR', charWidth: 7.2, lineHeight: 18, nodePadding: 10,
            minNodeWidth: 60, maxNodeWidth: 320, nodeSize: null
        }, options || {});
        const LR = o.direction === 'LR';

        const info = new Map();
        for (const n of nodes) {
            const size = (o.nodeSize && o.nodeSize(n)) || estimateSize(n.label != null ? n.label : n.id, o);
            info.set(n.id, { id: n.id, level: n.level || 0, w: size.w, h: size.h, deg: 0, up: [], down: [] });
        }
        const edgesBetween = new Map();     // "L1|L2" (L1 < L2) -> count
        for (const e of edges) {
            const a = info.get(e.from), b = info.get(e.to);
            if (!a || !b || a === b) continue;
            a.deg++; b.deg++;
            const lo = a.level <= b.level ? a : b, hi = lo === a ? b : a;
            if (lo.level !== hi.level) {
                hi.up.push(lo); lo.down.push(hi);
                const k = lo.level + '|' + hi.level;
                edgesBetween.set(k, (edgesBetween.get(k) || 0) + 1);
            }
        }

        const levelIds = [...new Set([...info.values()].map(n => n.level))].sort((a, b) => a - b);
        const byLevel = new Map(levelIds.map(l => [l, []]));
        for (const n of info.values()) byLevel.get(n.level).push(n);

        // Along-axis size = how far a level extends in the flow direction; across-axis = how nodes stack.
        const along = n => LR ? n.w : n.h;
        const across = n => LR ? n.h : n.w;

        // 1. order inside each level by barycentre of the neighbours (a few sweeps) to cut crossings
        const idx = new Map();
        const reindex = () => byLevel.forEach(list => list.forEach((n, i) => idx.set(n.id, i)));
        byLevel.forEach(list => list.sort((a, b) => String(a.id).localeCompare(String(b.id))));
        reindex();
        const bary = (n, nbrs) => nbrs.length ? nbrs.reduce((s, m) => s + idx.get(m.id), 0) / nbrs.length : idx.get(n.id);
        for (let pass = 0; pass < 4; pass++) {
            const order = pass % 2 === 0 ? levelIds : [...levelIds].reverse();
            for (const l of order) {
                const list = byLevel.get(l);
                const key = new Map(list.map(n => [n.id, bary(n, pass % 2 === 0 ? n.up : n.down)]));
                list.sort((a, b) => key.get(a.id) - key.get(b.id));
                reindex();
            }
        }

        // 2. spacing derived from the graph
        const sepOf = (l1, l2) => {
            const cnt = edgesBetween.get(l1 + '|' + l2) || 0;
            const n1 = byLevel.get(l1).length, n2 = byLevel.get(l2).length;
            // room for edge labels and bends grows with the edges crossing the gap, not with the node count alone
            return clamp(56 + 14 * Math.sqrt(cnt) + 2 * Math.min(n1, n2), 56, 220);
        };
        const gapOf = (list) => {
            if (list.length > 40) return 8;
            const avgDeg = list.reduce((s, n) => s + n.deg, 0) / Math.max(1, list.length);
            return clamp(10 + 3 * avgDeg, 10, 40);
        };

        // 3. position along the flow axis
        const thickness = new Map(levelIds.map(l => [l, Math.max(...byLevel.get(l).map(along))]));
        const flowPos = new Map();
        const zero = levelIds.includes(0) ? 0 : levelIds[0];
        flowPos.set(zero, 0);
        const levelGap = new Map();
        for (let i = levelIds.indexOf(zero) + 1; i < levelIds.length; i++) {
            const prev = levelIds[i - 1], cur = levelIds[i];
            const gap = sepOf(prev, cur);
            levelGap.set(prev + '|' + cur, gap);
            flowPos.set(cur, flowPos.get(prev) + thickness.get(prev) / 2 + gap + thickness.get(cur) / 2);
        }
        for (let i = levelIds.indexOf(zero) - 1; i >= 0; i--) {
            const next = levelIds[i + 1], cur = levelIds[i];
            const gap = sepOf(cur, next);
            levelGap.set(cur + '|' + next, gap);
            flowPos.set(cur, flowPos.get(next) - thickness.get(next) / 2 - gap - thickness.get(cur) / 2);
        }

        // 4. position across the flow axis: stack, then pull towards neighbours without overlapping
        const stackPos = new Map();
        for (const l of levelIds) {
            const list = byLevel.get(l), gap = gapOf(list);
            const total = list.reduce((s, n) => s + across(n), 0) + gap * (list.length - 1);
            let cursor = -total / 2;
            for (const n of list) { stackPos.set(n.id, cursor + across(n) / 2); cursor += across(n) + gap; }
        }
        const relax = (list, nbrsOf) => {
            const gap = gapOf(list);
            const want = list.map(n => {
                const ns = nbrsOf(n);
                return ns.length ? ns.reduce((s, m) => s + stackPos.get(m.id), 0) / ns.length : stackPos.get(n.id);
            });
            // keep the order, enforce the minimum gap, stay as close to the wanted position as possible
            let prevEnd = -Infinity;
            list.forEach((n, i) => {
                const half = across(n) / 2;
                const p = Math.max(want[i], prevEnd + gap + half);
                stackPos.set(n.id, p);
                prevEnd = p + half;
            });
            // pull back up if the sweep pushed the whole column away from its wanted centre
            const drift = list.reduce((s, n, i) => s + (stackPos.get(n.id) - want[i]), 0) / list.length;
            list.forEach(n => stackPos.set(n.id, stackPos.get(n.id) - drift));
        };
        for (let pass = 0; pass < 3; pass++) {
            for (const l of levelIds) relax(byLevel.get(l), n => n.up);
            for (const l of [...levelIds].reverse()) relax(byLevel.get(l), n => n.down);
        }

        const positions = new Map();
        let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
        for (const n of info.values()) {
            const f = flowPos.get(n.level), s = stackPos.get(n.id);
            const x = LR ? f : s, y = LR ? s : f;
            positions.set(n.id, { x, y, w: n.w, h: n.h });
            minX = Math.min(minX, x - n.w / 2); maxX = Math.max(maxX, x + n.w / 2);
            minY = Math.min(minY, y - n.h / 2); maxY = Math.max(maxY, y + n.h / 2);
        }
        return { positions, width: maxX - minX, height: maxY - minY, levelGap };
    }

    /** True when two laid-out boxes overlap (used by the tests). */
    function boxesOverlap(a, b) {
        return Math.abs(a.x - b.x) < (a.w + b.w) / 2 && Math.abs(a.y - b.y) < (a.h + b.h) / 2;
    }

    return { bfsLevels, reachableDepth, depthChoices, layoutLayered, boxesOverlap };
});
