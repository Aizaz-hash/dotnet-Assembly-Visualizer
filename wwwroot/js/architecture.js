// Architecture tab: dependencies between namespaces and the dependency cycles among namespaces and types.
// Built from the call graph and inheritance of the loaded assembly (calls and base types only - field and parameter
// types are not part of the analysis, so a dependency that exists only through those is not shown).
'use strict';

let archNetwork = null;
let archCache = null;                 // { data, classEdges, namespaces, nsCycles, typeCycles }
const archState = { onlyCycles: false, focus: null, typeView: null };

function archModel() {
    if (archCache && archCache.data === rawAssemblyData) return archCache;

    const classEdges = [];
    const seen = new Set();
    const add = (from, to) => {
        if (!nsOf.has(from) || !nsOf.has(to) || from === to) return;
        classEdges.push({ from, to });
        seen.add(from + SEP + to);
    };
    rawAssemblyData.methodGraph.edges.forEach(e => add(e.fromClass, e.toClass));
    rawAssemblyData.inheritance.edges.forEach(e => add(e.from, e.to));

    const namespaces = ArchitectureUtils.namespaceGraph(classEdges, nsOf);
    const nsCycles = ArchitectureUtils.findCycles(namespaces.nodes.map(n => n.id), namespaces.edges);

    // type-level cycles: de-duplicate edges first, they are many
    const uniq = [...seen].map(k => { const [from, to] = k.split(SEP); return { from, to }; });
    const typeCycles = ArchitectureUtils.findCycles([...nsOf.keys()], uniq);

    archCache = { data: rawAssemblyData, classEdges: uniq, namespaces, nsCycles, typeCycles };
    return archCache;
}

function archDestroy() { if (archNetwork) { archNetwork.destroy(); archNetwork = null; } }

function renderArchitecture() {
    archDestroy();
    const list = byId('archList'), canvas = byId('archCanvas');
    list.innerHTML = ''; canvas.innerHTML = '';
    byId('archHint').textContent = '';
    byId('btnArchBack').style.display = archState.typeView ? 'inline-block' : 'none';
    if (!hasAssembly) { canvasMessage(canvas, 'Open a .NET assembly to see how its namespaces depend on each other.'); return; }

    const m = archModel();
    // the filter only means something when there is a cycle to filter to
    const filter = byId('optArchCycles');
    filter.disabled = m.nsCycles.length === 0;
    filter.title = m.nsCycles.length === 0 ? 'No namespace is in a cycle, so there is nothing to filter to' : 'Show only the namespaces that are part of a cycle';
    if (m.nsCycles.length === 0 && archState.onlyCycles) { archState.onlyCycles = false; filter.checked = false; }
    if (m.namespaces.nodes.length < 2 && !archState.typeView) {
        canvasMessage(canvas, 'This assembly has a single namespace, so there are no dependencies between namespaces to show.');
    }

    renderArchList(list, m);
    if (archState.typeView) drawTypeCycle(canvas, m, archState.typeView);
    else if (m.namespaces.nodes.length >= 2) drawNamespaces(canvas, m);
}

function renderArchList(list, m) {
    const head = el('div', 'arch-head');
    head.appendChild(el('b', null, m.nsCycles.length ? '⚠ ' + m.nsCycles.length + ' namespace cycle' + (m.nsCycles.length === 1 ? '' : 's') : '✓ No cycles between namespaces'));
    list.appendChild(head);
    list.appendChild(el('div', 'view-hint', m.nsCycles.length
        ? 'Namespaces in a cycle depend on each other, directly or through others, so none of them can be understood, tested or released on its own. Click one to find it in the graph.'
        : 'Every namespace dependency points one way.'));

    m.nsCycles.slice(0, 50).forEach((c, i) => {
        const item = el('div', 'arch-cycle' + (archState.focus === i ? ' active' : ''));
        item.appendChild(el('div', 'ac-title', c.members.length + ' namespaces'));
        item.appendChild(el('div', 'ac-path', c.path.join(' → ')));
        item.onclick = () => { archState.focus = archState.focus === i ? null : i; archState.typeView = null; renderArchitecture(); };
        list.appendChild(item);
    });

    const det = el('details', 'arch-types');
    det.open = m.typeCycles.length > 0 && m.typeCycles.length <= 15;
    det.appendChild(el('summary', null, m.typeCycles.length + ' cycle' + (m.typeCycles.length === 1 ? '' : 's') + ' between types'));
    if (!m.typeCycles.length) det.appendChild(el('div', 'view-hint', 'No type depends on itself through calls or inheritance.'));
    m.typeCycles.slice(0, 100).forEach(c => {
        const item = el('div', 'arch-cycle');
        item.appendChild(el('div', 'ac-title', c.members.length + ' types'));
        item.appendChild(el('div', 'ac-path', c.path.map(shortName).join(' → ')));
        item.title = c.members.join('\n');
        item.onclick = () => { archState.typeView = c; renderArchitecture(); };
        det.appendChild(item);
    });
    if (m.typeCycles.length > 100) det.appendChild(el('div', 'view-hint', 'Showing the 100 largest.'));
    list.appendChild(det);
}

function archNodeBox(label, cyclic, selected) {
    return {
        label, shape: 'box', margin: 8, widthConstraint: { maximum: 300 }, borderWidth: selected ? 3 : 1.5,
        color: cyclic ? { background: Theme.colors().bad.background, border: selected ? Theme.colors().selBd : Theme.colors().bad.border } : { background: Theme.colors().ok.background, border: selected ? Theme.colors().selBd : Theme.colors().archOkBd },
        font: { face: 'Segoe UI, sans-serif', size: 14, color: cyclic ? Theme.colors().fontBad : Theme.colors().font }
    };
}

function archOptions() {
    return { layout: { hierarchical: { enabled: false } }, physics: false, interaction: { hover: true, tooltipDelay: 200 }, edges: { selectionWidth: 2 } };
}

function archDraw(canvas, nodes, edges) {
    const laid = GraphUtils.layoutLayered(nodes, edges, { direction: 'LR', charWidth: 7.2, maxNodeWidth: 320, lineHeight: 18, nodePadding: 8 });
    nodes.forEach(n => { const p = laid.positions.get(n.id); n.x = p.x; n.y = p.y; });
    archNetwork = new vis.Network(canvas, { nodes, edges }, archOptions());
    archNetwork.once('afterDrawing', () => archNetwork && archNetwork.fit({ animation: false, maxZoomLevel: 1.1 }));
    return archNetwork;
}

const ARCH_MAX_NODES = 200;

function drawNamespaces(canvas, m) {
    const cyclic = new Set(m.nsCycles.flatMap(c => c.members));
    let ids = m.namespaces.nodes.map(n => n.id);
    if (archState.onlyCycles) ids = ids.filter(id => cyclic.has(id));

    let note = '';
    if (ids.length > ARCH_MAX_NODES) {
        // keep the namespaces with the most dependencies in either direction
        const degree = new Map(ids.map(id => [id, 0]));
        m.namespaces.edges.forEach(e => { if (degree.has(e.from)) degree.set(e.from, degree.get(e.from) + e.count); if (degree.has(e.to)) degree.set(e.to, degree.get(e.to) + e.count); });
        ids = ids.sort((a, b) => degree.get(b) - degree.get(a)).slice(0, ARCH_MAX_NODES);
        note = ` · showing the ${ARCH_MAX_NODES} most connected of ${m.namespaces.nodes.length}`;
    }
    const keep = new Set(ids);
    const used = m.namespaces.edges.filter(e => keep.has(e.from) && keep.has(e.to));
    const layer = ArchitectureUtils.layers(ids, used);
    const focus = archState.focus != null ? new Set(m.nsCycles[archState.focus].members) : null;
    const typeCount = new Map(m.namespaces.nodes.map(n => [n.id, n.types]));

    const nodes = ids.map(id => Object.assign({ id, level: layer.get(id), title: id + '\n' + typeCount.get(id) + ' type' + (typeCount.get(id) === 1 ? '' : 's') },
        archNodeBox(id || '(global namespace)', cyclic.has(id), !!focus && focus.has(id))));
    const comp = new Map();
    m.nsCycles.forEach((c, i) => c.members.forEach(x => comp.set(x, i)));
    const edges = used.map(e => {
        const bad = comp.has(e.from) && comp.get(e.from) === comp.get(e.to);
        return {
            from: e.from, to: e.to, arrows: 'to', width: 1 + Math.min(4, Math.log2(e.count)),
            label: e.count > 1 ? String(e.count) : undefined, title: e.count + ' dependenc' + (e.count === 1 ? 'y' : 'ies') + ', e.g. ' + e.example,
            color: bad ? { color: Theme.colors().edgeBad, highlight: Theme.colors().edgeBadH } : { color: Theme.colors().edgeA, highlight: Theme.colors().edgeMH }, dashes: bad ? [7, 4] : false,
            font: { size: 10, color: Theme.colors().label, strokeWidth: 3, strokeColor: Theme.colors().labelStroke },
            smooth: { enabled: true, type: 'cubicBezier', forceDirection: 'horizontal', roundness: 0.45 }
        };
    });

    byId('archHint').textContent = `${nodes.length} namespaces · ${edges.length} dependencies · ${cyclic.size} in cycles` + note +
        ' · an arrow means "uses" - callers on the left, what they depend on to the right';
    const net = archDraw(canvas, nodes, edges);
    if (focus) net.once('afterDrawing', () => net.fit({ nodes: [...focus], animation: false, maxZoomLevel: 1.1 }));
}

function drawTypeCycle(canvas, m, cycle) {
    const members = new Set(cycle.members);
    const used = m.classEdges.filter(e => members.has(e.from) && members.has(e.to));
    const nodes = cycle.members.map(id => Object.assign({ id, level: 0, title: id }, archNodeBox(shortName(id), true, false)));
    const layer = ArchitectureUtils.layers(cycle.members, []);          // all in one cycle: spread them with the cycle path as the order
    cycle.path.slice(0, -1).forEach((id, i) => { const n = nodes.find(x => x.id === id); if (n) n.level = i; });
    nodes.forEach(n => { if (n.level === undefined) n.level = layer.get(n.id); });
    const edges = used.map(e => ({
        from: e.from, to: e.to, arrows: 'to', color: { color: Theme.colors().edgeBad, highlight: Theme.colors().edgeBadH }, dashes: [7, 4],
        smooth: { enabled: true, type: 'curvedCW', roundness: 0.3 }
    }));
    byId('archHint').textContent = `${nodes.length} types in one cycle · ${edges.length} dependencies · double-click a type to open its methods`;
    const net = archDraw(canvas, nodes, edges);
    net.on('doubleClick', (p) => { if (p.nodes.length) navigate({ view: 'navigator', type: p.nodes[0], method: null }); });
}

byId('btnArchBack').onclick = () => { archState.typeView = null; renderArchitecture(); };
byId('optArchCycles').addEventListener('change', () => { archState.onlyCycles = byId('optArchCycles').checked; archState.focus = null; renderArchitecture(); });
byId('btnFitArch').onclick = () => { if (archNetwork) archNetwork.fit({ animation: { duration: 300 }, maxZoomLevel: 1.1 }); };
