// ============================================================================
//  dotnet Assembly Visualizer - ILSpy-style explorer
//  Data comes from POST /api/analysis/analyze:
//    { classes[], inheritance{nodes,edges}, methodGraph{nodes,edges}, deadlocks[][] }
// ============================================================================

let rawAssemblyData = { classes: [], inheritance: { nodes: [], edges: [] }, methodGraph: { nodes: [], edges: [] }, deadlocks: [] };
let hasAssembly = false;
let currentAssemblyName = '';
let maxUploadMb = null;
let messageTimeout = null;

// Previously analysed assemblies of this session (name -> analysis result).
const loadedAssemblies = new Map();
const loadedFiles = new Map();      // name -> File, so an assembly can be reloaded
const MAX_LOADED_ASSEMBLIES = 8;

const MAX_CALL_NODES = 120;        // cap for a single call graph
const TREE_CHUNK = 300;            // children rendered before a "show more" row
const MAX_CHIPS = 40;

// Derived indexes (rebuilt per assembly)
let typeNodes = new Map();         // typeId -> { id, name, type }
let basesOf = new Map();           // typeId -> [{ id, relation }]
let derivedOf = new Map();         // typeId -> [{ id, relation }]
let nsOf = new Map();              // typeId -> namespace
let typesByNs = new Map();         // ns -> [typeId]
let methodById = new Map();        // methodId -> node
let methodsByClass = new Map();    // typeId -> [node]
let calleesOf = new Map();         // methodId -> [methodId]
let callersOf = new Map();         // methodId -> [methodId]
let edgeCounts = new Map();        // "from\u0001to" -> count
let cycles = [];                   // [{ body: [methodId...] }]  (deduplicated, rotation-normalised)
let cyclesOfMethod = new Map();    // methodId -> [cycle index]
let cyclicEdges = new Set();       // "from\u0001to"

// Navigation state
let state = { view: 'inheritance', type: null, method: null, cycleMethod: null, cycleIdx: 0, inhMode: 'selected', inhFormat: 'graph' };
let history = [];
let historyIdx = -1;

// Graph instances
let callNetwork = null;
let cycleNetwork = null;
let inhNetwork = null;
const INH_MAX_NODES = 300;
let chips = [];
let explorerNodes = new Map();     // key -> tree handle
let selectedExplorerKey = null;
let layoutTimeout = null;

const SEP = '\u0001';

// ---------- small helpers ----------

function el(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
}

function byId(id) { return document.getElementById(id); }

function truncate(text, max) {
    return text.length > max ? text.substring(0, max - 1) + '…' : text;
}

function shortName(fullName) {
    if (!fullName) return '';
    const i = fullName.lastIndexOf('.');
    return i < 0 ? fullName : fullName.substring(i + 1);
}

// "ns.Class.Method(System.String s)" -> { className: "ns.Class", methodName: "Method(System.String s)" }
function splitMethodId(id) {
    const paren = id.indexOf('(');
    const head = paren > -1 ? id.substring(0, paren) : id;
    const tail = paren > -1 ? id.substring(paren) : '';
    const lastDot = head.lastIndexOf('.');
    if (lastDot < 0) return { className: '', methodName: id };
    return { className: head.substring(0, lastDot), methodName: head.substring(lastDot + 1) + tail };
}

function methodInfo(id) {
    const node = methodById.get(id);
    if (node) return { name: node.name, className: node.parentClass, external: false };
    const p = splitMethodId(id);
    return { name: p.methodName, className: p.className || 'External Reference', external: true };
}

function setStatus(text) { byId('statusText').textContent = text; }

function showMessage(text, type = 'info') {
    const win = byId('msgWindow');
    if (messageTimeout) clearTimeout(messageTimeout);
    win.className = type === 'error' ? 'msg-error' : (type === 'success' ? 'msg-success' : 'msg-info');
    win.style.display = 'flex';
    byId('msgText').textContent = text;
    if (type !== 'error') messageTimeout = setTimeout(closeMessage, 5000);
}
function closeMessage() { byId('msgWindow').style.display = 'none'; }

function showLoader(text) {
    byId('loaderStatusText').textContent = text;
    byId('canvasLoaderOverlay').style.display = 'flex';
}
function hideLoader() {
    byId('canvasLoaderOverlay').style.display = 'none';
    if (layoutTimeout) { clearTimeout(layoutTimeout); layoutTimeout = null; }
}

function canvasMessage(container, text, buttonText, onButton) {
    container.innerHTML = '';
    const box = el('div', 'canvas-message');
    box.appendChild(el('div', null, text));
    if (buttonText) {
        const btn = el('button', 'canvas-message-btn', buttonText);
        btn.onclick = onButton;
        box.appendChild(btn);
    }
    container.appendChild(box);
}

// ---------- indexes ----------

function rotateTo(body, methodId) {
    const i = body.indexOf(methodId);
    return i <= 0 ? body.slice() : body.slice(i).concat(body.slice(0, i));
}

function canonicalKey(body) {
    let best = null;
    for (let i = 0; i < body.length; i++) {
        const k = body.slice(i).concat(body.slice(0, i)).join(SEP);
        if (best === null || k < best) best = k;
    }
    return best;
}

function buildIndexes() {
    const d = rawAssemblyData;
    typeNodes = new Map(); basesOf = new Map(); derivedOf = new Map(); nsOf = new Map(); typesByNs = new Map();
    methodById = new Map(); methodsByClass = new Map(); calleesOf = new Map(); callersOf = new Map();
    edgeCounts = new Map(); cycles = []; cyclesOfMethod = new Map(); cyclicEdges = new Set();

    d.inheritance.nodes.forEach(n => typeNodes.set(n.id, n));
    d.classes.forEach(fullName => {
        if (!typeNodes.has(fullName)) typeNodes.set(fullName, { id: fullName, name: shortName(fullName), type: 'Class' });
        const parts = fullName.split('.');
        parts.pop();
        const ns = parts.join('.').trim();
        nsOf.set(fullName, ns);
        if (!typesByNs.has(ns)) typesByNs.set(ns, []);
        typesByNs.get(ns).push(fullName);
    });
    typesByNs.forEach(list => list.sort((a, b) => typeNodes.get(a).name.localeCompare(typeNodes.get(b).name)));

    d.inheritance.edges.forEach(e => {
        if (e.to === 'GenericTypeSpecification') return;   // analyzer placeholder for generic instantiations (e.g. IEnumerable<T>), not a real type
        if (!basesOf.has(e.from)) basesOf.set(e.from, []);
        if (!derivedOf.has(e.to)) derivedOf.set(e.to, []);
        if (!basesOf.get(e.from).some(x => x.id === e.to)) basesOf.get(e.from).push({ id: e.to, relation: e.relation });
        if (!derivedOf.get(e.to).some(x => x.id === e.from)) derivedOf.get(e.to).push({ id: e.from, relation: e.relation });
    });

    d.methodGraph.nodes.forEach(n => {
        methodById.set(n.id, n);
        if (!methodsByClass.has(n.parentClass)) methodsByClass.set(n.parentClass, []);
        methodsByClass.get(n.parentClass).push(n);
    });
    methodsByClass.forEach(list => list.sort((a, b) => a.name.localeCompare(b.name)));

    // Merge duplicate call edges into one edge with a count.
    d.methodGraph.edges.forEach(e => {
        const key = e.from + SEP + e.to;
        edgeCounts.set(key, (edgeCounts.get(key) || 0) + 1);
        if (edgeCounts.get(key) === 1) {
            if (!calleesOf.has(e.from)) calleesOf.set(e.from, []);
            if (!callersOf.has(e.to)) callersOf.set(e.to, []);
            calleesOf.get(e.from).push(e.to);
            callersOf.get(e.to).push(e.from);
        }
    });

    // Circular chains: drop duplicates of the same cycle that merely start at a different method.
    const seen = new Set();
    (d.deadlocks || []).forEach(raw => {
        if (!raw || raw.length === 0) return;
        const body = raw.length > 1 && raw[0] === raw[raw.length - 1] ? raw.slice(0, -1) : raw.slice();
        const key = canonicalKey(body);
        if (seen.has(key)) return;
        seen.add(key);
        const idx = cycles.length;
        cycles.push({ body });
        body.forEach((m, i) => {
            if (!cyclesOfMethod.has(m)) cyclesOfMethod.set(m, []);
            cyclesOfMethod.get(m).push(idx);
            cyclicEdges.add(m + SEP + body[(i + 1) % body.length]);
        });
    });
}

// ============================================================================
//  Tree widget (used by the explorer and the inheritance view)
// ============================================================================

function makeTreeNode(opt) {
    const node = el('div', 't-node');
    const row = el('div', 't-row' + (opt.external ? ' external' : ''));
    const arrow = el('span', 't-arrow' + (opt.children ? '' : ' leaf'), '▸');
    row.appendChild(arrow);
    if (opt.icon) row.appendChild(el('span', 'ico ' + opt.icon));
    const label = el('span', 't-label', opt.label);
    row.appendChild(label);
    if (opt.sub) row.appendChild(el('span', 't-sub', opt.sub));
    if (opt.tag) row.appendChild(el('span', 't-tag', opt.tag));
    if (opt.cycleCount) {
        const b = el('span', 't-badge-cycle', '⚠');
        b.title = `Part of ${opt.cycleCount} circular call chain${opt.cycleCount === 1 ? '' : 's'} - click to view`;
        b.onclick = (e) => { e.stopPropagation(); if (opt.onCycleClick) opt.onCycleClick(); };
        row.appendChild(b);
    }
    if (opt.onClose) {
        const x = el('span', 't-close', '✕');
        x.title = opt.closeTitle || 'Remove';
        x.onclick = (e) => { e.stopPropagation(); opt.onClose(handle); };
        row.appendChild(x);
    }
    if (opt.bold) label.style.fontWeight = '600';
    if (opt.title) row.title = opt.title;
    node.appendChild(row);

    let childBox = null, open = false;

    const renderKids = (kids, start) => {
        const end = Math.min(kids.length, start + TREE_CHUNK);
        for (let i = start; i < end; i++) childBox.appendChild(makeTreeNode(kids[i]).el);
        if (end < kids.length) {
            const more = el('div', 't-more', `Show ${Math.min(TREE_CHUNK, kids.length - end)} more of ${kids.length - end} remaining…`);
            more.style.cursor = 'pointer';
            more.onclick = () => { more.remove(); renderKids(kids, end); };
            childBox.appendChild(more);
        }
    };

    const handle = {
        el: node, row, opt,
        get isOpen() { return open; },
        expand() {
            if (!opt.children) return;
            if (!childBox) {
                childBox = el('div', 't-children');
                node.appendChild(childBox);
                const kids = opt.children();
                if (kids.length === 0) childBox.appendChild(el('div', 't-more', '(none)'));
                else renderKids(kids, 0);
            }
            childBox.classList.remove('collapsed');
            arrow.textContent = '▾';
            open = true;
        },
        collapse() {
            if (childBox) childBox.classList.add('collapsed');
            arrow.textContent = '▸';
            open = false;
        },
        toggle() { open ? handle.collapse() : handle.expand(); },
        setSelected(on) { row.classList.toggle('selected', on); }
    };

    arrow.onclick = (e) => { e.stopPropagation(); handle.toggle(); };
    row.onclick = () => { if (opt.onSelect) opt.onSelect(handle); };
    row.ondblclick = () => handle.toggle();
    if (opt.onContextMenu) row.oncontextmenu = (e) => { e.preventDefault(); e.stopPropagation(); opt.onContextMenu(e, handle); };
    if (opt.key) explorerNodes.set(opt.key, handle);
    if (opt.expanded) handle.expand();
    return handle;
}

// ============================================================================
//  Explorer (left tree)
// ============================================================================

function typeIcon(typeId) {
    const t = typeNodes.get(typeId);
    return t && t.type === 'Interface' ? 'ico-iface' : 'ico-class';
}

function typeLinkOpt(ref, extra) {
    const t = typeNodes.get(ref.id);
    return {
        icon: t ? typeIcon(ref.id) : 'ico-ext',
        label: t ? t.name : shortName(ref.id),
        sub: t ? (nsOf.get(ref.id) || '') : ref.id,
        tag: ref.relation ? ref.relation.toLowerCase() : undefined,
        external: !t,
        title: ref.id,
        onSelect: t ? () => navigate({ view: 'inheritance', type: ref.id, method: null }) : undefined,
        ...extra
    };
}

function methodOpt(m) {
    const n = (cyclesOfMethod.get(m.id) || []).length;
    return {
        key: 'm:' + m.id,
        icon: 'ico-method',
        label: m.name,
        title: m.id,
        cycleCount: n,
        onCycleClick: () => navigate({ view: 'cycles', cycleMethod: m.id, cycleIdx: 0 }),
        onSelect: () => navigate({ view: 'calls', type: m.parentClass, method: m.id })
    };
}

function typeOpt(typeId) {
    const t = typeNodes.get(typeId);
    return {
        key: 't:' + typeId,
        icon: typeIcon(typeId),
        label: t.name,
        title: typeId,
        children: () => {
            const kids = [];
            const bases = basesOf.get(typeId) || [];
            const derived = derivedOf.get(typeId) || [];
            if (bases.length) kids.push({ icon: 'ico-folder', label: 'Base Types', sub: String(bases.length), children: () => bases.map(b => typeLinkOpt(b)) });
            if (derived.length) kids.push({ icon: 'ico-folder', label: 'Derived Types', sub: String(derived.length), children: () => derived.map(b => typeLinkOpt(b)) });
            (methodsByClass.get(typeId) || []).forEach(m => kids.push(methodOpt(m)));
            return kids;
        },
        onSelect: () => navigate({ view: 'inheritance', type: typeId, method: null })
    };
}

function buildExplorer() {
    explorerNodes = new Map();
    selectedExplorerKey = null;
    const tree = byId('explorerTree');
    tree.innerHTML = '';
    byId('explorerTitle').textContent = loadedAssemblies.size > 1 ? 'Assemblies (' + loadedAssemblies.size + ')' : 'Assemblies';

    if (!loadedAssemblies.size) {
        tree.appendChild(el('div', 'empty-note', 'No assemblies loaded. Click "Open" or drop a .dll / .exe onto the page.'));
        return;
    }

    // Every loaded assembly is listed (like ILSpy); the active one is expanded to its namespaces and types.
    const nsList = hasAssembly ? Array.from(typesByNs.keys()).filter(ns => !ns.startsWith('<')).sort() : [];   // hide compiler-generated pseudo-namespaces
    loadedAssemblies.forEach((data, name) => {
        const active = hasAssembly && name === currentAssemblyName;
        const opt = {
            key: active ? 'asm' : 'asm:' + name,
            icon: 'ico-asm',
            label: name,
            sub: data.classes.length + ' types',
            title: name + ' - right-click for Reload / Remove',
            bold: active,
            onClose: () => removeAssembly(name),
            closeTitle: 'Remove "' + name + '" from the list',
            onContextMenu: (e) => showAssemblyMenu(e, name),
            onSelect: (h) => { if (active) h.toggle(); else activateAssembly(name); }
        };
        if (active) {
            opt.expanded = true;
            opt.children = () => nsList.map(ns => ({
                key: 'n:' + ns,
                icon: 'ico-ns',
                label: ns || '(Global Namespace)',
                sub: String(typesByNs.get(ns).length),
                title: ns || '(Global Namespace)',
                children: () => typesByNs.get(ns).map(typeOpt)
            }));
        }
        tree.appendChild(makeTreeNode(opt).el);
    });
}

function revealInExplorer(key) {
    if (selectedExplorerKey && explorerNodes.has(selectedExplorerKey)) explorerNodes.get(selectedExplorerKey).setSelected(false);
    selectedExplorerKey = null;
    if (!key) return;

    let typeId = null, methodId = null;
    if (key.startsWith('m:')) { methodId = key.substring(2); typeId = (methodById.get(methodId) || {}).parentClass; }
    else if (key.startsWith('t:')) typeId = key.substring(2);

    if (typeId && typeNodes.has(typeId)) {
        const ns = nsOf.get(typeId) || '';
        const nsNode = explorerNodes.get('n:' + ns);
        if (nsNode) nsNode.expand();
        if (methodId) { const tn = explorerNodes.get('t:' + typeId); if (tn) tn.expand(); }
    }
    const h = explorerNodes.get(key);
    if (h) {
        h.setSelected(true);
        selectedExplorerKey = key;
        h.row.scrollIntoView({ block: 'nearest' });
    }
}

function renderSearchResults(query) {
    const tree = byId('explorerTree');
    tree.innerHTML = '';
    const q = query.toLowerCase();
    const types = [], methods = [];
    typeNodes.forEach((t, id) => { if (types.length < 100 && id.toLowerCase().includes(q) && (nsOf.has(id))) types.push(id); });
    for (const m of methodById.values()) {
        if (methods.length >= 100) break;
        if (m.id.toLowerCase().includes(q)) methods.push(m);
    }
    if (!types.length && !methods.length) { tree.appendChild(el('div', 'empty-note', 'No matches.')); return; }
    if (types.length) {
        tree.appendChild(el('div', 'pane-title', `Types (${types.length}${types.length === 100 ? '+' : ''})`));
        types.forEach(id => {
            const opt = typeOpt(id); delete opt.children; delete opt.key;
            opt.sub = nsOf.get(id) || '';
            tree.appendChild(makeTreeNode(opt).el);
        });
    }
    if (methods.length) {
        tree.appendChild(el('div', 'pane-title', `Methods (${methods.length}${methods.length === 100 ? '+' : ''})`));
        methods.forEach(m => {
            const opt = methodOpt(m); delete opt.key;
            opt.sub = shortName(m.parentClass);
            tree.appendChild(makeTreeNode(opt).el);
        });
    }
}

// ============================================================================
//  Navigation (with back / forward)
// ============================================================================

function sameState(a, b) {
    return a.view === b.view && a.type === b.type && a.method === b.method &&
        a.cycleMethod === b.cycleMethod && a.cycleIdx === b.cycleIdx && a.inhMode === b.inhMode && a.inhFormat === b.inhFormat;
}

function navigate(patch, push = true) {
    const next = { ...state, ...patch };
    if (sameState(next, state) && push) { applyState(); return; }
    state = next;
    if (push) {
        history = history.slice(0, historyIdx + 1);
        history.push({ ...state });
        historyIdx = history.length - 1;
    }
    applyState();
}

function goHistory(delta) {
    const target = historyIdx + delta;
    if (target < 0 || target >= history.length) return;
    historyIdx = target;
    state = { ...history[historyIdx] };
    applyState();
}

function updateNavButtons() {
    byId('btnBack').disabled = historyIdx <= 0;
    byId('btnForward').disabled = historyIdx >= history.length - 1;
}

function applyState() {
    ['inheritance', 'calls', 'cycles'].forEach(v => {
        byId('view' + v.charAt(0).toUpperCase() + v.slice(1)).classList.toggle('active', state.view === v);
        byId('tab' + v.charAt(0).toUpperCase() + v.slice(1)).classList.toggle('active', state.view === v);
    });
    byId('inhModeSelected').classList.toggle('active', state.inhMode === 'selected');
    byId('inhModeAll').classList.toggle('active', state.inhMode === 'all');
    byId('inhFmtGraph').classList.toggle('active', state.inhFormat === 'graph');
    byId('inhFmtTree').classList.toggle('active', state.inhFormat === 'tree');

    revealInExplorer(state.method ? 'm:' + state.method : (state.type ? 't:' + state.type : null));
    renderTitle();

    if (state.view === 'inheritance') renderInheritance();
    else if (state.view === 'calls') renderCalls();
    else renderCycles();
    updateNavButtons();
}

function cycleLink(methodId) {
    const n = (cyclesOfMethod.get(methodId) || []).length;
    if (!n) return null;
    const a = el('span', 'cycle-link', `⚠ Circular Dependency Detected${n > 1 ? ` (${n})` : ''}`);
    a.title = 'Show the circular call chain(s) this method belongs to';
    a.onclick = () => navigate({ view: 'cycles', cycleMethod: methodId, cycleIdx: 0 });
    return a;
}

function renderTitle() {
    const t = byId('viewTitle');
    t.innerHTML = '';
    if (!hasAssembly) { t.appendChild(el('span', 'vt-sub', 'No assembly loaded')); return; }

    if (state.view === 'cycles') {
        t.appendChild(el('span', 'vt-name', 'Circular Dependencies'));
        t.appendChild(el('span', 'vt-sub', `${cycles.length} distinct circular call chain${cycles.length === 1 ? '' : 's'} in ${currentAssemblyName}`));
        return;
    }
    if (state.view === 'calls' && state.method) {
        const info = methodInfo(state.method);
        const toInheritance = () => { if (typeNodes.has(info.className)) navigate({ view: 'inheritance', type: info.className, method: null }); };
        const back = el('span', 'crumb', '◀ Inheritance');
        back.title = 'Back to the inheritance view of ' + info.className;
        back.onclick = toInheritance;
        t.appendChild(back);
        t.appendChild(el('span', 'ico ico-method'));
        const name = el('span', 'vt-name', truncate(info.name, 90));
        name.title = info.className + '.' + info.name;
        t.appendChild(name);
        const decl = el('span', 'vt-sub crumb', 'declared in ' + info.className);
        decl.title = 'Open this type in the inheritance view';
        decl.onclick = toInheritance;
        t.appendChild(decl);
        return;
    }
    if (state.type) {
        const tn = typeNodes.get(state.type);
        t.appendChild(el('span', 'ico ' + typeIcon(state.type)));
        t.appendChild(el('span', 'vt-name', tn ? tn.name : shortName(state.type)));
        t.appendChild(el('span', 'vt-sub', `${tn && tn.type === 'Interface' ? 'interface' : 'class'} · ${nsOf.get(state.type) || '(Global Namespace)'}`));
        return;
    }
    t.appendChild(el('span', 'vt-sub', 'Select a type or method in the explorer.'));
}

// ============================================================================
//  Inheritance view
// ============================================================================

function methodLeafOpt(m) {
    const o = methodOpt(m);
    delete o.key;                       // the explorer owns the registered keys
    return o;
}

function methodsOf(typeId) {
    return methodsByClass.get(typeId) || [];
}

function methodsFolderOpt(typeId) {
    const methods = methodsOf(typeId);
    if (!methods.length) return null;
    return { icon: 'ico-folder', label: 'Methods', sub: String(methods.length), children: () => methods.map(methodLeafOpt) };
}

function inhDepthValue() {
    return parseInt(byId('inhDepth').value, 10) || 1;
}

function inheritanceOpt(ref, direction, ancestors, level = 1) {
    const t = typeNodes.get(ref.id);
    const loop = ancestors.has(ref.id);
    const nextAnc = new Set(ancestors); nextAnc.add(ref.id);
    const list = direction === 'up' ? (basesOf.get(ref.id) || []) : (derivedOf.get(ref.id) || []);
    return typeLinkOpt(ref, {
        sub: t ? (nsOf.get(ref.id) || '') : ref.id,
        children: (methodsOf(ref.id).length || (!loop && list.length)) ? () => {
            const kids = [];
            const folder = methodsFolderOpt(ref.id);
            if (folder) kids.push(folder);
            if (!loop) list.forEach(r => kids.push(inheritanceOpt(r, direction, nextAnc, level + 1)));
            return kids;
        } : undefined,
        // Depth = how many levels of the hierarchy are opened automatically (you can still expand further by hand).
        expanded: !loop && list.length > 0 && level < inhDepthValue(),
        onSelect: t ? () => navigate({ view: 'inheritance', type: ref.id, method: null }) : undefined
    });
}

// ---------- inheritance: graph ----------

function inhNodeStyle(id, selected) {
    const t = typeNodes.get(id);
    const iface = !!t && t.type === 'Interface';
    const external = !t;
    let color = iface
        ? { background: '#e3f2fd', border: '#1e88e5', highlight: { background: '#cfe8fc', border: '#1565c0' } }
        : { background: '#fff3e0', border: '#fb8c00', highlight: { background: '#ffe0b2', border: '#e65100' } };
    if (external) color = { background: '#f5f5f5', border: '#9e9e9e', highlight: { background: '#e4e4e4', border: '#757575' } };
    if (selected) color = { background: iface ? '#e3f2fd' : '#fff3e0', border: '#007bff', highlight: { background: iface ? '#cfe8fc' : '#ffe0b2', border: '#0056b3' } };
    return {
        id, label: t ? t.name : shortName(id), shape: iface ? 'ellipse' : 'box', margin: 12,
        borderWidth: selected ? 3 : 1.5, color,
        font: { face: 'Segoe UI, sans-serif', size: 15, color: external ? '#6b6b6b' : '#1e1e1e' },
        title: `${id}${external ? '\n(not in this assembly)' : ''}`
    };
}

function renderInheritanceGraph() {
    const canvas = byId('inhCanvas');
    canvas.innerHTML = '';
    byId('inhHint').textContent = '';

    if (!hasAssembly) { canvasMessage(canvas, 'Upload a .NET assembly to begin: click "Open" or drop a .dll file anywhere on this page.'); return; }

    const depth = inhDepthValue();
    const levels = new Map();   // typeId -> level (negative = base types above, positive = derived types below)
    let truncated = false;
    const add = (id, lvl) => {
        if (levels.has(id)) return false;
        if (levels.size >= INH_MAX_NODES) { truncated = true; return false; }
        levels.set(id, lvl);
        return true;
    };

    if (state.inhMode === 'all') {
        const roots = [];
        typeNodes.forEach((t, id) => {
            if (!nsOf.has(id)) return;
            const hasBase = (basesOf.get(id) || []).some(b => typeNodes.has(b.id));
            if (!hasBase && (derivedOf.get(id) || []).length) roots.push(id);
        });
        roots.sort((a, b) => typeNodes.get(a).name.localeCompare(typeNodes.get(b).name));
        let frontier = [];
        roots.forEach(r => { if (add(r, 0)) frontier.push(r); });
        for (let d = 1; frontier.length && d <= depth; d++) {
            const next = [];
            frontier.forEach(id => (derivedOf.get(id) || []).forEach(c => { if (typeNodes.has(c.id) && add(c.id, d)) next.push(c.id); }));
            frontier = next;
        }
        if (levels.size === 0) { canvasMessage(canvas, 'No type in this assembly derives from or implements another type of the same assembly.'); return; }
    } else {
        if (!state.type) { canvasMessage(canvas, 'Select a type in the explorer to draw its inheritance graph.'); return; }
        levels.set(state.type, 0);
        let up = [state.type], down = [state.type];
        for (let d = 1; d <= depth && (up.length || down.length); d++) {
            const nu = [], nd = [];
            up.forEach(id => (basesOf.get(id) || []).forEach(b => { if (add(b.id, -d)) nu.push(b.id); }));
            down.forEach(id => (derivedOf.get(id) || []).forEach(c => { if (add(c.id, d)) nd.push(c.id); }));
            up = nu; down = nd;
        }
    }

    const nodes = [], edges = [];
    levels.forEach((lvl, id) => {
        const n = inhNodeStyle(id, state.inhMode !== 'all' && id === state.type);
        n.level = lvl;
        nodes.push(n);
    });
    levels.forEach((_, id) => (basesOf.get(id) || []).forEach(b => {
        if (!levels.has(b.id)) return;
        const implementsRel = !!b.relation && b.relation.toLowerCase() === 'implements';
        edges.push({
            from: id, to: b.id, arrows: 'to',
            label: b.relation ? b.relation.toLowerCase() : undefined,
            dashes: implementsRel ? [6, 4] : false,
            color: { color: '#7a7a7a', highlight: '#333' },
            font: { size: 12, color: '#555', strokeWidth: 3, strokeColor: '#fcfcfc', align: 'middle' },
            smooth: { enabled: true, type: 'cubicBezier', forceDirection: 'vertical', roundness: 0.5 }
        });
    }));

    let reached = 0;
    levels.forEach(l => { reached = Math.max(reached, Math.abs(l)); });
    byId('inhHint').textContent = `${nodes.length} types · ${edges.length} relations · depth ${depth}` +
        (truncated ? ` · limited to ${INH_MAX_NODES} types - use "Selected type" for the full picture`
            : (reached < depth ? ` · nothing deeper than ${reached} level${reached === 1 ? '' : 's'}` : ' · double-click a type to centre on it'));

    const options = {
        layout: { hierarchical: { enabled: true, direction: 'UD', sortMethod: 'directed', levelSeparation: 110, nodeSpacing: 240, treeSpacing: 240, blockShifting: true, edgeMinimization: true, parentCentralization: true } },
        physics: false,
        interaction: { hover: true, tooltipDelay: 200 },
        edges: { selectionWidth: 2 }
    };
    inhNetwork = new vis.Network(canvas, { nodes, edges }, options);
    inhNetwork.once('afterDrawing', () => inhNetwork && inhNetwork.fit({ animation: false, maxZoomLevel: 1.2 }));
    inhNetwork.on('click', (p) => { if (p.nodes.length) setStatus(p.nodes[0]); });
    inhNetwork.on('doubleClick', (p) => {
        if (!p.nodes.length) return;
        const id = p.nodes[0];
        if (!typeNodes.has(id)) { showMessage('That type is outside the analysed assembly.', 'info'); return; }
        navigate({ view: 'inheritance', inhMode: 'selected', type: id, method: null });
    });
}

function renderMembersPanel() {
    const panel = byId('inhMembers');
    const show = state.inhFormat !== 'tree' && state.inhMode !== 'all' && !!state.type && hasAssembly;
    panel.style.display = show ? 'flex' : 'none';
    if (!show) return;
    const methods = methodsOf(state.type);
    byId('inhMembersTitle').textContent = 'Methods (' + methods.length + ') - click to open';
    const list = byId('inhMembersList');
    list.innerHTML = '';
    if (!methods.length) { list.appendChild(el('div', 'empty-note', 'This type has no executable methods.')); return; }
    methods.forEach(m => list.appendChild(makeTreeNode(methodLeafOpt(m)).el));
}

function renderInheritance() {
    if (inhNetwork) { inhNetwork.destroy(); inhNetwork = null; }
    const graph = state.inhFormat !== 'tree';
    byId('inhGraphRow').style.display = graph ? 'flex' : 'none';
    byId('inheritanceBody').style.display = graph ? 'none' : 'block';
    byId('inhLegend').style.display = graph ? 'flex' : 'none';
    byId('btnFitInh').style.display = graph ? 'inline-block' : 'none';
    byId('btnExportInh').style.display = graph ? 'inline-block' : 'none';
    byId('inhDepthLabel').style.display = 'flex';
    if (graph) { byId('inhHint').textContent = ''; renderInheritanceGraph(); }
    else { byId('inhHint').textContent = 'Click a type to navigate to it · ▸ expands'; renderInheritanceTree(); }
    renderMembersPanel();
}

function renderInheritanceTree() {
    const body = byId('inheritanceBody');
    body.innerHTML = '';
    if (!hasAssembly) { body.appendChild(el('div', 'empty-note', 'Upload a .NET assembly to begin: click "Open" or drop a .dll file anywhere on this page.')); return; }

    if (state.inhMode === 'all') {
        const roots = [];
        typeNodes.forEach((t, id) => {
            if (!nsOf.has(id)) return;
            const bases = (basesOf.get(id) || []).filter(b => typeNodes.has(b.id));
            if (bases.length === 0) roots.push(id);
        });
        roots.sort((a, b) => typeNodes.get(a).name.localeCompare(typeNodes.get(b).name));
        const withKids = roots.filter(id => (derivedOf.get(id) || []).length);
        const section = el('div', 'inh-section');
        section.appendChild(el('div', 'inh-head', `Type hierarchies (${withKids.length} root types with subtypes; ${roots.length - withKids.length} standalone types hidden)`));
        const anc = new Set();
        const wrapper = makeTreeNode({
            icon: 'ico-asm', label: currentAssemblyName, expanded: true,
            children: () => withKids.map(id => inheritanceOpt({ id }, 'down', anc))
        });
        section.appendChild(wrapper.el);
        body.appendChild(section);
        return;
    }

    if (!state.type) { body.appendChild(el('div', 'empty-note', 'Select a type in the explorer to see its base types and derived types.')); return; }

    const self = el('div', 'inh-section inh-self');
    {
        const count = methodsOf(state.type).length;
        const label = (nsOf.get(state.type) || '') + (count ? ' · ' + count + ' method' + (count === 1 ? '' : 's') : '');
        self.appendChild(makeTreeNode(typeLinkOpt({ id: state.type }, {
            sub: label, onSelect: undefined, tag: undefined,
            children: count ? () => methodsOf(state.type).map(methodLeafOpt) : undefined
        })).el);
    }
    body.appendChild(self);

    const anc = new Set([state.type]);
    const bases = basesOf.get(state.type) || [];
    const derived = derivedOf.get(state.type) || [];

    const sBase = el('div', 'inh-section');
    sBase.appendChild(el('div', 'inh-head', `Base Types (${bases.length})`));
    if (bases.length === 0) sBase.appendChild(el('div', 'empty-note', 'No base types or interfaces in this assembly (derives from System.Object).'));
    bases.forEach(b => sBase.appendChild(makeTreeNode({ ...inheritanceOpt(b, 'up', anc) }).el));
    body.appendChild(sBase);

    const sDer = el('div', 'inh-section');
    sDer.appendChild(el('div', 'inh-head', `Derived Types (${derived.length})`));
    if (derived.length === 0) sDer.appendChild(el('div', 'empty-note', 'No types in this assembly derive from or implement this type.'));
    derived.forEach(b => sDer.appendChild(makeTreeNode({ ...inheritanceOpt(b, 'down', anc) }).el));
    body.appendChild(sDer);
}

// ============================================================================
//  Method calls view
// ============================================================================

function destroyCallNetwork() {
    if (callNetwork) { callNetwork.destroy(); callNetwork = null; }
    chips = [];
    byId('chipLayer').innerHTML = '';
}

function nodeStyle(id, selected) {
    const info = methodInfo(id);
    const cyclic = cyclesOfMethod.has(id);
    const label = `${cyclic ? '⚠ ' : ''}${shortName(info.className)}\n➔ ${truncate(info.name, 40)}`;
    let color = { background: '#e8f5e9', border: '#4caf50', highlight: { background: '#c8e6c9', border: '#2e7d32' } };
    if (info.external) color = { background: '#f5f5f5', border: '#9e9e9e', highlight: { background: '#e4e4e4', border: '#757575' } };
    if (cyclic) color = { background: '#ffebeb', border: '#dc3545', highlight: { background: '#f8d7da', border: '#bd2130' } };
    if (selected) color = { background: cyclic ? '#ffebeb' : (info.external ? '#f5f5f5' : '#e8f5e9'), border: '#007bff', highlight: { background: cyclic ? '#f8d7da' : '#c8e6c9', border: '#0056b3' } };
    return {
        id, label, shape: 'box', margin: 10,
        borderWidth: selected ? 3 : 1.5,
        color, font: { face: 'Segoe UI, sans-serif', size: 14, multi: false, color: cyclic ? '#721c24' : '#212529' },
        title: `${info.className}.${info.name}${cyclic ? '\n⚠ part of a circular call chain' : ''}${info.external ? '\n(external / not in this assembly)' : ''}`
    };
}

function buildCallGraph(center, depth, wantCallers, wantCallees) {
    const levels = new Map([[center, 0]]);
    let truncated = false;

    const bfs = (adj, dir) => {
        let frontier = [center];
        for (let d = 1; d <= depth && frontier.length; d++) {
            const next = [];
            for (const id of frontier) {
                for (const n of (adj.get(id) || [])) {
                    if (levels.has(n)) continue;           // merged: a method appears once, however many paths reach it
                    if (levels.size >= MAX_CALL_NODES) { truncated = true; continue; }
                    levels.set(n, dir * d);
                    next.push(n);
                }
            }
            frontier = next;
        }
    };
    if (wantCallees) bfs(calleesOf, +1);
    if (wantCallers) bfs(callersOf, -1);

    const nodes = [], edges = [];
    levels.forEach((lvl, id) => {
        const n = nodeStyle(id, id === center);
        n.level = lvl;
        nodes.push(n);
    });
    levels.forEach((_, id) => {
        for (const c of (calleesOf.get(id) || [])) {
            if (!levels.has(c)) continue;
            const key = id + SEP + c;
            const count = edgeCounts.get(key) || 1;
            const cyc = cyclicEdges.has(key);
            edges.push({
                from: id, to: c, arrows: 'to',
                label: count > 1 ? `×${count}` : undefined,
                color: cyc ? { color: '#dc3545', highlight: '#bd2130' } : { color: '#8a8a8a', highlight: '#444' },
                dashes: cyc ? [7, 4] : false,
                width: cyc ? 2 : 1,
                font: { size: 10, color: '#555', strokeWidth: 3, strokeColor: '#fcfcfc' },
                smooth: (levels.get(id) >= levels.get(c))
                    ? { enabled: true, type: 'curvedCW', roundness: 0.45 }          // back-edges (recursion / cycles) arc over the forward flow
                    : { enabled: true, type: 'cubicBezier', forceDirection: 'horizontal', roundness: 0.5 }
            });
        }
    });
    let reached = 0;
    levels.forEach(l => { reached = Math.max(reached, Math.abs(l)); });
    return { nodes, edges, truncated, reached };
}

// Shown only when the dependency analysis found this method in a circular call chain.
function renderMethodNote() {
    const note = byId('methodNote');
    note.innerHTML = '';
    const n = (state.view === 'calls' && state.method) ? (cyclesOfMethod.get(state.method) || []).length : 0;
    if (!n) { note.style.display = 'none'; return; }
    note.appendChild(el('span', 'mn-icon', 'ⓘ'));
    note.appendChild(document.createTextNode('This method may have a circular dependency. Click '));
    const link = el('a', 'mn-link', 'Circular Dependency View');
    link.href = '#';
    link.title = 'Show the ' + n + ' circular call chain' + (n === 1 ? '' : 's') + ' through this method';
    link.onclick = (e) => { e.preventDefault(); navigate({ view: 'cycles', cycleMethod: state.method, cycleIdx: 0 }); };
    note.appendChild(link);
    note.appendChild(document.createTextNode(' to investigate.'));
    note.style.display = 'block';
}

function renderCalls() {
    destroyCallNetwork();
    hideLoader();
    renderMethodNote();
    const canvas = byId('callCanvas');
    canvas.innerHTML = '';
    byId('callStats').textContent = '';

    if (!hasAssembly) { canvasMessage(canvas, 'Upload a .NET assembly to begin.'); return; }

    if (!state.method) {
        if (!state.type) { canvasMessage(canvas, 'Select a method in the explorer (expand a type) to see its callers and callees.'); return; }
        // method picker for the selected type
        const methods = methodsByClass.get(state.type) || [];
        canvas.innerHTML = '';
        const box = el('div', 'scroll-body');
        box.style.position = 'absolute'; box.style.inset = '0';
        if (!methods.length) box.appendChild(el('div', 'empty-note', 'This type has no executable methods.'));
        else {
            box.appendChild(el('div', 'inh-head', `Methods of ${typeNodes.get(state.type).name} (${methods.length}) - pick one:`));
            methods.forEach(m => box.appendChild(makeTreeNode((() => { const o = methodOpt(m); delete o.key; return o; })()).el));
        }
        canvas.appendChild(box);
        return;
    }

    const depth = parseInt(byId('callDepth').value, 10);
    const g = buildCallGraph(state.method, depth, byId('optCallers').checked, byId('optCallees').checked);
    const callees = (calleesOf.get(state.method) || []).length, callers = (callersOf.get(state.method) || []).length;
    byId('callStats').textContent = `${g.nodes.length} methods · ${g.edges.length} calls · ${callers} direct caller${callers === 1 ? '' : 's'}, ${callees} direct callee${callees === 1 ? '' : 's'}` + ` · depth ${depth}` +
        (g.truncated ? ` · limited to ${MAX_CALL_NODES} nodes - lower the depth`
            : (g.reached < depth ? ` · nothing deeper than ${g.reached} level${g.reached === 1 ? '' : 's'}` : ''));

    const options = {
        layout: { hierarchical: { enabled: true, direction: 'LR', sortMethod: 'directed', levelSeparation: 230, nodeSpacing: 36, treeSpacing: 60, blockShifting: true, edgeMinimization: true, parentCentralization: true } },
        physics: false,
        interaction: { hover: true, tooltipDelay: 200, navigationButtons: false },
        edges: { selectionWidth: 2 }
    };
    canvas.innerHTML = '';
    if (g.nodes.length > 1) showLoader(`Laying out ${g.nodes.length} methods…`);
    setTimeout(() => {
        if (state.view !== 'calls' || !state.method) { hideLoader(); return; }
        callNetwork = new vis.Network(canvas, { nodes: g.nodes, edges: g.edges }, options);
        callNetwork.once('afterDrawing', () => { hideLoader(); callNetwork && callNetwork.fit({ animation: false, maxZoomLevel: 1.2 }); });
        layoutTimeout = setTimeout(hideLoader, 8000);

        // Circular Dependency chips (HTML overlay positioned above cyclic nodes)
        const layer = byId('chipLayer');
        layer.innerHTML = '';
        chips = [];
        g.nodes.filter(n => cyclesOfMethod.has(n.id)).slice(0, MAX_CHIPS).forEach(n => {
            const isMain = n.id === state.method;
            const chip = el('div', 'cyc-chip' + (isMain ? ' main' : ''), isMain ? '⚠ Circular Dependency Detected' : '⚠ Circular');
            chip.title = 'Click to view the circular call chain(s) through this method';
            chip.onclick = () => navigate({ view: 'cycles', cycleMethod: n.id, cycleIdx: 0 });
            layer.appendChild(chip);
            chips.push({ id: n.id, el: chip });
        });
        const updateChips = () => {
            if (!callNetwork) return;
            chips.forEach(c => {
                try {
                    const bb = callNetwork.getBoundingBox(c.id);
                    const p = callNetwork.canvasToDOM({ x: (bb.left + bb.right) / 2, y: bb.top });
                    c.el.style.left = p.x + 'px';
                    c.el.style.top = (p.y - 3) + 'px';
                    c.el.style.display = 'block';
                } catch { c.el.style.display = 'none'; }
            });
        };
        callNetwork.on('afterDrawing', updateChips);

        callNetwork.on('click', (p) => {
            if (p.nodes.length) setStatus(p.nodes[0]);
        });
        callNetwork.on('doubleClick', (p) => {
            if (p.nodes.length && p.nodes[0] !== state.method) {
                const id = p.nodes[0];
                const info = methodInfo(id);
                if (info.external) { showMessage('That method is outside the analysed assembly.', 'info'); return; }
                navigate({ view: 'calls', type: info.className, method: id });
            }
        });
    }, 30);
}

function fitCallGraph() {
    const net = state.view === 'cycles' ? cycleNetwork : (state.view === 'inheritance' ? inhNetwork : callNetwork);
    if (net) net.fit({ animation: { duration: 300 }, maxZoomLevel: 1.2 });
}

function exportGraphPng() {
    const canvas = document.querySelector(state.view === 'cycles' ? '#cycleCanvas canvas' : (state.view === 'inheritance' ? '#inhCanvas canvas' : '#callCanvas canvas'));
    if (!canvas) { showMessage('Nothing to export yet - draw a graph first.', 'info'); return; }
    const out = document.createElement('canvas');
    out.width = canvas.width; out.height = canvas.height;
    const ctx = out.getContext('2d');
    ctx.fillStyle = '#ffffff';
    ctx.fillRect(0, 0, out.width, out.height);
    ctx.drawImage(canvas, 0, 0);
    const a = document.createElement('a');
    a.href = out.toDataURL('image/png');
    a.download = `${state.view}-graph.png`;
    a.click();
}

// ============================================================================
//  Circular dependency view
// ============================================================================

function filteredCycleList() {
    const out = [];
    cycles.forEach((c, i) => {
        if (state.cycleMethod && !c.body.includes(state.cycleMethod)) return;
        out.push({ index: i, body: state.cycleMethod ? rotateTo(c.body, state.cycleMethod) : c.body });
    });
    return out;
}

function methodToken(id, cls) {
    const info = methodInfo(id);
    const span = el('span', 'chain-method' + (cls ? ' ' + cls : ''), truncate(info.name.replace(/\(.*$/, '') + '()', 40));
    span.title = `${info.className}.${info.name}\nClick to open in Method Calls`;
    span.onclick = () => {
        if (info.external) { showMessage('That method is outside the analysed assembly.', 'info'); return; }
        navigate({ view: 'calls', type: info.className, method: id });
    };
    return span;
}

function renderCycles() {
    if (cycleNetwork) { cycleNetwork.destroy(); cycleNetwork = null; }
    const list = byId('cycleList'), chain = byId('cycleChain'), canvas = byId('cycleCanvas');
    list.innerHTML = ''; chain.innerHTML = ''; canvas.innerHTML = '';
    const filter = byId('cycleFilter');

    if (!hasAssembly) { list.appendChild(el('div', 'empty-note', 'Upload a .NET assembly to begin.')); filter.style.display = 'none'; return; }

    if (state.cycleMethod) {
        const info = methodInfo(state.cycleMethod);
        filter.style.display = 'inline-block';
        filter.innerHTML = '';
        filter.appendChild(document.createTextNode(`Through: ${truncate(info.name.replace(/\(.*$/, '') + '()', 36)} `));
        const x = el('button', null, '×'); x.title = 'Show all circular chains';
        x.onclick = () => navigate({ cycleMethod: null, cycleIdx: 0 });
        filter.appendChild(x);
    } else filter.style.display = 'none';

    const items = filteredCycleList();
    byId('cycleStats').textContent = `${items.length} of ${cycles.length} chain${cycles.length === 1 ? '' : 's'} shown`;

    if (!items.length) {
        list.appendChild(el('div', 'empty-note', cycles.length ? 'No circular chains for this method.' : '✓ No circular call chains were detected in this assembly.'));
        return;
    }
    if (state.cycleIdx >= items.length) state.cycleIdx = 0;

    // group by the class of the chain's first method (or one group when filtered to a method)
    let lastGroup = null;
    const ordered = state.cycleMethod ? items : items.slice().sort((a, b) => methodInfo(a.body[0]).className.localeCompare(methodInfo(b.body[0]).className));
    const positionOf = new Map(items.map((it, i) => [it, i]));
    ordered.forEach((it, n) => {
        const group = state.cycleMethod ? `Chains through ${methodInfo(state.cycleMethod).name.replace(/\(.*$/, '')}()` : methodInfo(it.body[0]).className;
        if (group !== lastGroup) { list.appendChild(el('div', 'cyc-group-head', group)); lastGroup = group; }
        const pos = positionOf.get(it);
        const row = el('div', 'cyc-item' + (pos === state.cycleIdx ? ' selected' : ''));
        const top = el('div', 'ci-top');
        top.appendChild(el('span', null, `Chain #${it.index + 1}`));
        top.appendChild(el('span', null, `${it.body.length} call${it.body.length === 1 ? '' : 's'}`));
        row.appendChild(top);
        row.appendChild(el('div', 'ci-path', it.body.concat([it.body[0]]).map(m => methodInfo(m).name.replace(/\(.*$/, '') + '()').join(' → ')));
        row.onclick = () => navigate({ cycleIdx: pos });
        list.appendChild(row);
    });

    const active = items[state.cycleIdx];
    renderCycleDetail(active.body, chain, canvas);
}

function renderCycleDetail(body, chain, canvas) {
    // Text chain: A → B → C → A, with the start and the loop-back marked.
    body.forEach((id, i) => {
        const step = el('span', 'chain-step');
        step.appendChild(methodToken(id, i === 0 ? 'start' : ''));
        if (i === 0) step.appendChild(el('span', 'chain-note start', 'start'));
        const cls = el('span', 'chain-class', ` (${shortName(methodInfo(id).className)})`);
        step.appendChild(cls);
        step.appendChild(el('span', 'chain-arrow', '→'));
        chain.appendChild(step);
    });
    const back = el('span', 'chain-step');
    back.appendChild(methodToken(body[0], 'start'));
    back.appendChild(el('span', 'chain-note loop', '↺ loops back to start'));
    chain.appendChild(back);

    // Graph: nodes on a circle, clockwise from the top; start node highlighted.
    const n = body.length;
    const radius = Math.max(110, n * 38);
    const nodes = body.map((id, i) => {
        const angle = -Math.PI / 2 + (2 * Math.PI * i) / n;
        const st = nodeStyle(id, false);
        st.x = Math.cos(angle) * radius * 1.5; st.y = Math.sin(angle) * radius;
        st.fixed = true;
        st.color = i === 0
            ? { background: '#d4edda', border: '#28a745', highlight: { background: '#c3e6cb', border: '#1e7e34' } }
            : { background: '#ffebeb', border: '#dc3545', highlight: { background: '#f8d7da', border: '#bd2130' } };
        st.borderWidth = i === 0 ? 3 : 2;
        st.label = (i === 0 ? '▶ start\n' : '') + `${shortName(methodInfo(id).className)}\n➔ ${truncate(methodInfo(id).name, 36)}`;
        return st;
    });
    const edges = body.map((id, i) => {
        const to = body[(i + 1) % n];
        const closing = i === n - 1;
        return {
            from: id, to, arrows: 'to', width: 2.5,
            label: closing ? `${i + 1} ↺` : String(i + 1),
            color: { color: '#dc3545', highlight: '#bd2130' },
            dashes: closing ? [7, 4] : false,
            font: { size: 13, color: '#721c24', strokeWidth: 3, strokeColor: '#fafafa' },
            smooth: n === 1 ? { type: 'curvedCW', roundness: 0.6 } : { type: 'curvedCW', roundness: 0.18 }
        };
    });
    canvas.innerHTML = '';
    const holder = el('div'); holder.style.position = 'absolute'; holder.style.inset = '0';
    canvas.appendChild(holder);
    cycleNetwork = new vis.Network(holder, { nodes, edges }, { physics: false, interaction: { hover: true, tooltipDelay: 200 } });
    cycleNetwork.once('afterDrawing', () => cycleNetwork && cycleNetwork.fit({ animation: false, maxZoomLevel: 1.2 }));
    cycleNetwork.on('doubleClick', (p) => {
        if (!p.nodes.length) return;
        const info = methodInfo(p.nodes[0]);
        if (!info.external) navigate({ view: 'calls', type: info.className, method: p.nodes[0] });
    });
}

// ============================================================================
//  Loading an assembly
// ============================================================================

// ---------- assembly list: context menu, reload, remove ----------

function hideContextMenu() {
    const m = byId('ctxMenu');
    if (m) m.style.display = 'none';
}

function showAssemblyMenu(e, name) {
    const m = byId('ctxMenu');
    m.innerHTML = '';
    const add = (text, fn, danger) => {
        const item = el('div', 'ctx-item' + (danger ? ' danger' : ''), text);
        item.onclick = (ev) => { ev.stopPropagation(); hideContextMenu(); fn(); };
        m.appendChild(item);
    };
    m.appendChild(el('div', 'ctx-title', name));
    add('⟳  Reload', () => reloadAssembly(name));
    add('✕  Remove', () => removeAssembly(name), true);
    if (loadedAssemblies.size > 1) {
        m.appendChild(el('div', 'ctx-sep'));
        add('Remove all other assemblies', () => removeOtherAssemblies(name), true);
    }
    m.style.display = 'block';
    const x = Math.min(e.clientX, window.innerWidth - m.offsetWidth - 6);
    const y = Math.min(e.clientY, window.innerHeight - m.offsetHeight - 6);
    m.style.left = Math.max(4, x) + 'px';
    m.style.top = Math.max(4, y) + 'px';
}

function clearAssembly() {
    hasAssembly = false;
    currentAssemblyName = '';
    rawAssemblyData = { classes: [], inheritance: { nodes: [], edges: [] }, methodGraph: { nodes: [], edges: [] }, deadlocks: [] };
    buildIndexes();
    state = { view: 'inheritance', type: null, method: null, cycleMethod: null, cycleIdx: 0, inhMode: state.inhMode, inhFormat: state.inhFormat };
    history = [{ ...state }];
    historyIdx = 0;
    byId('currentDllName').textContent = 'No assembly loaded';
    byId('currentDllName').title = '';
    byId('searchBox').value = '';
    byId('searchBox').disabled = true;
    const badge = byId('cycleCount');
    badge.textContent = '0';
    badge.classList.remove('has');
    buildExplorer();
    applyState();
    setStatus('Ready');
}

function removeAssembly(name) {
    if (!loadedAssemblies.has(name)) return;
    const wasActive = hasAssembly && name === currentAssemblyName;
    loadedAssemblies.delete(name);
    loadedFiles.delete(name);
    if (wasActive) {
        const remaining = Array.from(loadedAssemblies.keys());
        if (remaining.length) activateAssembly(remaining[remaining.length - 1]);
        else clearAssembly();
    } else {
        buildExplorer();
        revealInExplorer(state.method ? 'm:' + state.method : (state.type ? 't:' + state.type : null));
    }
    showMessage('Removed "' + name + '" from the list.', 'info');
}

function removeOtherAssemblies(keepName) {
    Array.from(loadedAssemblies.keys()).forEach(n => { if (n !== keepName) { loadedAssemblies.delete(n); loadedFiles.delete(n); } });
    if (!(hasAssembly && currentAssemblyName === keepName)) activateAssembly(keepName);
    else { buildExplorer(); revealInExplorer(state.method ? 'm:' + state.method : (state.type ? 't:' + state.type : null)); }
    showMessage('Kept only "' + keepName + '".', 'info');
}

async function reloadAssembly(name) {
    const file = loadedFiles.get(name);
    if (!file) {
        showMessage('"' + name + '" can no longer be read from memory - choose the file again with Open.', 'error');
        return;
    }
    await loadFile(file, { reload: true });
}

function activateAssembly(name) {
    rawAssemblyData = loadedAssemblies.get(name);
    currentAssemblyName = name;
    hasAssembly = true;
    buildIndexes();

    byId('currentDllName').textContent = name;
    byId('currentDllName').title = name;
    byId('searchBox').disabled = false;
    byId('searchBox').value = '';
    const badge = byId('cycleCount');
    badge.textContent = String(cycles.length);
    badge.classList.toggle('has', cycles.length > 0);

    state = { view: 'inheritance', type: null, method: null, cycleMethod: null, cycleIdx: 0, inhMode: state.inhMode, inhFormat: state.inhFormat };
    history = [{ ...state }];
    historyIdx = 0;
    buildExplorer();
    applyState();
    setStatus(`${name}: ${rawAssemblyData.classes.length} types, ${rawAssemblyData.methodGraph.nodes.length} methods, ${cycles.length} circular chain${cycles.length === 1 ? '' : 's'}`);
}

async function readErrorMessage(response) {
    let text = '';
    try { text = await response.text(); } catch { /* ignore */ }
    if (text) {
        try {
            const body = JSON.parse(text);
            if (body && body.errors) {
                const msgs = Object.values(body.errors).flat();
                if (msgs.length) return msgs.join(' ');
            }
            if (body && (body.detail || body.title)) return body.detail || body.title;
        } catch { /* plain text body */ }
        return text;
    }
    if (response.status === 413) return `File too large (limit ${maxUploadMb ?? '?'} MB).`;
    return `Server returned ${response.status} ${response.statusText}.`;
}

async function loadFile(file, opts = {}) {
    if (!file) return false;
    if (!/\.(dll|exe)$/i.test(file.name)) { showMessage(`"${file.name}" is not a .dll or .exe file.`, 'error'); return false; }
    if (maxUploadMb && file.size > maxUploadMb * 1024 * 1024) {
        showMessage(`"${file.name}" is ${(file.size / 1048576).toFixed(1)} MB; the upload limit is ${maxUploadMb} MB.`, 'error');
        return false;
    }

    setStatus(`Analysing ${file.name}…`);
    document.body.style.cursor = 'progress';
    const formData = new FormData();
    formData.append('file', file);

    try {
        const response = await fetch('/api/analysis/analyze', { method: 'POST', body: formData });
        if (!response.ok) throw new Error(await readErrorMessage(response));
        const data = await response.json();

        loadedAssemblies.set(file.name, data);     // an existing entry keeps its position in the list
        loadedFiles.set(file.name, file);
        let dropped = null;
        while (loadedAssemblies.size > MAX_LOADED_ASSEMBLIES) {
            dropped = loadedAssemblies.keys().next().value;
            loadedAssemblies.delete(dropped);
            loadedFiles.delete(dropped);
        }

        activateAssembly(file.name);
        const verb = opts.reload ? 'Reloaded' : 'Parsed';
        showMessage(`${verb} ${data.classes.length} types, ${data.methodGraph.nodes.length} methods, ${cycles.length} circular chains.` +
            (dropped ? ` The oldest assembly ("${dropped}") was removed from the list (limit ${MAX_LOADED_ASSEMBLIES}).` : ''), 'success');
        return true;
    } catch (err) {
        const reason = err instanceof TypeError
            ? (opts.reload
                ? 'the file could not be read again (moved, renamed or changed?). Use Open to choose it again.'
                : `the connection was closed - the file may exceed the server's upload limit${maxUploadMb ? ` (${maxUploadMb} MB)` : ''}.`)
            : err.message;
        showMessage(`Could not ${opts.reload ? 'reload' : 'analyse'} "${file.name}": ${reason}`, 'error');
        setStatus('Ready');
        return false;
    } finally {
        document.body.style.cursor = '';
    }
}

// ============================================================================
//  Wiring
// ============================================================================

byId('dllFile').addEventListener('change', async (e) => {
    await loadFile(e.target.files[0]);
    e.target.value = '';
});

document.querySelectorAll('.tab').forEach(tab => tab.addEventListener('click', () => {
    if (!hasAssembly) return;
    const view = tab.dataset.view;
    navigate({ view, ...(view === 'cycles' ? { cycleMethod: null, cycleIdx: 0 } : {}) });
}));
byId('inhModeSelected').onclick = () => navigate({ inhMode: 'selected' });
byId('inhModeAll').onclick = () => navigate({ inhMode: 'all' });
byId('inhFmtGraph').onclick = () => navigate({ inhFormat: 'graph' });
byId('inhFmtTree').onclick = () => navigate({ inhFormat: 'tree' });
byId('inhDepth').addEventListener('change', () => { if (state.view === 'inheritance') renderInheritance(); });
byId('btnFitInh').onclick = fitCallGraph;
byId('btnExportInh').onclick = exportGraphPng;

byId('btnBack').onclick = () => goHistory(-1);
byId('btnForward').onclick = () => goHistory(+1);
window.addEventListener('keydown', (e) => {
    if (e.altKey && e.key === 'ArrowLeft') { e.preventDefault(); goHistory(-1); }
    if (e.altKey && e.key === 'ArrowRight') { e.preventDefault(); goHistory(+1); }
});

['callDepth', 'optCallers', 'optCallees'].forEach(id => byId(id).addEventListener('change', () => { if (state.view === 'calls') renderCalls(); }));
byId('btnFit').onclick = fitCallGraph;
byId('btnExport').onclick = exportGraphPng;

let searchTimer = null;
byId('searchBox').addEventListener('input', (e) => {
    clearTimeout(searchTimer);
    const q = e.target.value.trim();
    searchTimer = setTimeout(() => {
        if (q.length < 2) { buildExplorer(); revealInExplorer(state.method ? 'm:' + state.method : (state.type ? 't:' + state.type : null)); return; }
        renderSearchResults(q);
    }, 150);
});

document.addEventListener('click', hideContextMenu);
document.addEventListener('contextmenu', (e) => { if (!e.target.closest || !e.target.closest('#explorerTree .t-row')) hideContextMenu(); });
window.addEventListener('keydown', (e) => { if (e.key === 'Escape') hideContextMenu(); });
window.addEventListener('blur', hideContextMenu);
window.addEventListener('resize', hideContextMenu);
byId('explorerTree').addEventListener('scroll', hideContextMenu);

// Explorer splitter
(function () {
    const splitter = byId('splitter'), explorer = byId('explorer');
    let dragging = false;
    splitter.addEventListener('mousedown', (e) => { dragging = true; splitter.classList.add('drag'); e.preventDefault(); });
    window.addEventListener('mousemove', (e) => { if (dragging) explorer.style.width = Math.max(180, Math.min(e.clientX, window.innerWidth * 0.7)) + 'px'; });
    window.addEventListener('mouseup', () => { dragging = false; splitter.classList.remove('drag'); });
})();

// Drag & drop a DLL anywhere on the page.
let dragDepth = 0;
window.addEventListener('dragenter', (e) => {
    if (!e.dataTransfer || !Array.from(e.dataTransfer.types).includes('Files')) return;
    dragDepth++;
    document.body.classList.add('dragging');
});
window.addEventListener('dragleave', () => { dragDepth = Math.max(0, dragDepth - 1); if (dragDepth === 0) document.body.classList.remove('dragging'); });
window.addEventListener('dragover', (e) => e.preventDefault());
window.addEventListener('drop', async (e) => {
    e.preventDefault();
    dragDepth = 0;
    document.body.classList.remove('dragging');
    await loadFile(e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files[0]);
});

fetch('/api/analysis/limits').then(r => r.ok ? r.json() : null).then(info => {
    if (!info) return;
    maxUploadMb = info.maxFileSizeMB;
    byId('uploadHint').textContent = `.dll / .exe, max ${maxUploadMb} MB`;
}).catch(() => { /* hint is optional */ });

buildExplorer();
applyState();
