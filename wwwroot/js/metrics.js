// Metrics tab: cyclomatic complexity, size, fan-in / fan-out and unreferenced methods, computed from the same IL scan
// as the call graph. Nothing is sent anywhere.
'use strict';

let metricsSection = 'hotspots';
let deadFilter = '';

const METRIC_SECTIONS = [
    ['hotspots', 'Complexity hotspots'],
    ['mostCalled', 'Most called'],
    ['mostCalling', 'Most dependent'],
    ['types', 'Largest types'],
    ['dead', 'Unreferenced methods']
];

function complexityClass(c) { return c > 20 ? 'cx-high' : c > 10 ? 'cx-mid' : 'cx-low'; }

function methodLinkCell(row) {
    const td = el('td');
    const a = el('a', 'rc-caller', (row.parentClass || '').split('.').pop() + '.' + row.name);
    a.href = '#'; a.title = row.id;
    a.onclick = (e) => { e.preventDefault(); navigate({ view: 'calls', type: row.parentClass, method: row.id }); };
    td.appendChild(a);
    return td;
}

function metricsTable(headers, rows) {
    const table = el('table', 'vuln-table small');
    const head = el('tr'); headers.forEach(h => head.appendChild(el('th', null, h)));
    const thead = el('thead'); thead.appendChild(head); table.appendChild(thead);
    const body = el('tbody'); rows.forEach(r => body.appendChild(r)); table.appendChild(body);
    return table;
}

function renderMetrics() {
    const root = byId('metricsScroll');
    root.innerHTML = '';
    if (!hasAssembly) { root.appendChild(el('div', 'empty-note', 'Open a .NET assembly to see its code metrics.')); return; }

    const m = rawAssemblyData.metrics;
    if (!m) { root.appendChild(el('div', 'empty-note', 'This analysis has no metrics (it was produced by an older version). Reload the assembly.')); return; }
    const dead = rawAssemblyData.deadCode || [];

    renderAssemblyInfo(root, rawAssemblyData.assembly);
    renderObfuscation(root, rawAssemblyData.obfuscation);

    root.appendChild(el('p', 'risky-intro', 'Measured from the code of ' + currentAssemblyName + ' (nothing runs). Complexity is cyclomatic complexity: one path plus every branch, switch case and catch clause - above 10 is hard to test, above 20 is a candidate to split.'));

    const counts = el('div', 'risky-counts');
    [['Methods with code', m.methods.toLocaleString()], ['Average complexity', m.averageComplexity], ['Highest complexity', m.maxComplexity],
     ['IL size', m.totalIlBytes < 10240 ? (m.totalIlBytes / 1024).toFixed(1) + ' KB' : Math.round(m.totalIlBytes / 1024).toLocaleString() + ' KB'], ['Unreferenced', dead.length + (dead.length >= 1000 ? '+' : '')]]
        .forEach(([label, value]) => {
            const c = el('div', 'risky-count'); c.appendChild(el('div', 'rcn-num', String(value))); c.appendChild(el('div', 'rcn-label', label)); counts.appendChild(c);
        });
    root.appendChild(counts);

    // complexity distribution
    const hist = el('div', 'cx-hist');
    const peak = Math.max(1, ...m.histogram.map(b => b.count));
    m.histogram.forEach((b, i) => {
        const row = el('div', 'cx-hist-row');
        row.appendChild(el('span', 'cx-hist-label', 'complexity ' + b.label));
        const bar = el('span', 'cx-hist-bar ' + (i >= 3 ? 'cx-high' : i === 2 ? 'cx-mid' : 'cx-low'));
        bar.style.width = Math.max(2, Math.round(b.count / peak * 280)) + 'px';
        row.appendChild(bar); row.appendChild(el('span', 'cx-hist-count', b.count.toLocaleString()));
        hist.appendChild(row);
    });
    root.appendChild(hist);

    const nav = el('div', 'seg');
    METRIC_SECTIONS.forEach(([key, label]) => {
        const b = el('button', 'seg-btn' + (metricsSection === key ? ' active' : ''), label + (key === 'dead' ? ' (' + dead.length + ')' : ''));
        b.onclick = () => { metricsSection = key; renderMetrics(); };
        nav.appendChild(b);
    });
    nav.style.margin = '14px 0 10px';
    root.appendChild(nav);

    const rowOf = (r, extra) => { const tr = el('tr'); tr.appendChild(methodLinkCell(r)); extra.forEach(v => tr.appendChild(v instanceof Node ? v : el('td', null, String(v)))); return tr; };
    const cxCell = (c) => el('td', 'cx-cell ' + complexityClass(c), String(c));

    if (metricsSection === 'hotspots') {
        root.appendChild(metricsTable(['Method', 'Complexity', 'IL bytes', 'Callers', 'Calls'], m.hotspots.map(r => rowOf(r, [cxCell(r.complexity), r.ilSize, r.callers, r.callees]))));
    } else if (metricsSection === 'mostCalled') {
        root.appendChild(el('div', 'view-hint', 'Methods many others depend on: a change here has the widest reach.'));
        root.appendChild(metricsTable(['Method', 'Callers', 'Complexity'], m.mostCalled.map(r => rowOf(r, [r.callers, cxCell(r.complexity)]))));
    } else if (metricsSection === 'mostCalling') {
        root.appendChild(el('div', 'view-hint', 'Methods that call many others: they know too much, or orchestrate.'));
        root.appendChild(metricsTable(['Method', 'Calls', 'Complexity'], m.mostCalling.map(r => rowOf(r, [r.callees, cxCell(r.complexity)]))));
    } else if (metricsSection === 'types') {
        const rows = m.types.map(t => { const tr = el('tr'); tr.appendChild(el('td', null, t.name)); tr.appendChild(el('td', null, String(t.methods))); tr.appendChild(cxCell(t.complexity)); tr.appendChild(el('td', null, (t.ilSize / 1024).toFixed(1) + ' KB')); return tr; });
        root.appendChild(metricsTable(['Type', 'Methods', 'Total complexity', 'IL size'], rows));
    } else {
        root.appendChild(el('div', 'vuln-banner warn', 'Private and internal methods that nothing in this assembly calls or takes the address of. They are leads, not proof: code called only by reflection, serialization, a framework attribute or another assembly (InternalsVisibleTo) can appear here. Virtual methods, constructors, entry points and methods with attributes are never listed.'));
        const search = el('input'); search.type = 'search'; search.placeholder = 'Filter…'; search.value = deadFilter; search.style.margin = '8px 0';
        const host = el('div');
        const draw = () => {
            host.innerHTML = '';
            const q = deadFilter.trim().toLowerCase();
            const rows = dead.filter(d => !q || d.id.toLowerCase().includes(q))
                .map(d => rowOf({ id: d.id, name: d.name, parentClass: d.parentClass }, [d.access, d.complexity, d.ilSize]));
            host.appendChild(rows.length ? metricsTable(['Method', 'Access', 'Complexity', 'IL bytes'], rows) : el('div', 'empty-note', dead.length ? 'Nothing matches.' : 'No unreferenced private or internal methods were found.'));
        };
        search.oninput = () => { deadFilter = search.value; draw(); };
        root.appendChild(search); root.appendChild(host); draw();
    }
}

// What the assembly is: facts from its metadata header.
function renderAssemblyInfo(root, a) {
    if (!a) return;
    const rows = [
        ['Assembly', a.name + ' ' + a.version], ['Type', a.kind], ['Platform', a.platform], ['Target framework', a.targetFramework ? a.targetFramework + (a.targetFrameworkInferred ? ' (guessed from its references: the file does not record one)' : '') : 'not recorded'],
        ['Strong-named', a.strongNamed ? 'yes' : 'no'], ['Entry point', a.hasEntryPoint ? 'yes' : 'no'],
        ['Build', a.debugBuild ? 'debug (contains debug information)' : 'release / no debug information'],
        ['References', a.references + ' assemblies'], ['File size', a.sizeBytes < 1048576 ? (a.sizeBytes / 1024).toFixed(1) + ' KB' : (a.sizeBytes / 1048576).toFixed(1) + ' MB']
    ];
    const box = el('details', 'asm-info'); box.open = true;
    box.appendChild(el('summary', null, 'About this assembly'));
    const grid = el('div', 'asm-grid');
    rows.forEach(([k, v]) => { grid.appendChild(el('span', 'asm-k', k)); grid.appendChild(el('span', 'asm-v', String(v))); });
    box.appendChild(grid);
    root.appendChild(box);
}

// Obfuscation indicators: evidence, never a verdict. The wording says what a clean result does NOT prove.
function renderObfuscation(root, o) {
    if (!o) return;
    const box = el('div', 'vuln-banner ' + (o.likely ? 'warn' : 'info'));
    box.appendChild(el('div', 'vb-title', o.likely ? '⚠ Obfuscation indicators found' : 'Obfuscation indicators: none found'));
    if (o.likely) {
        const ul = el('ul'); o.evidence.forEach(e => ul.appendChild(el('li', null, e))); box.appendChild(ul);
        if (o.examples && o.examples.length) box.appendChild(el('div', 'view-hint', 'Examples: ' + o.examples.join(', ')));
    }
    box.appendChild(el('div', null, o.note));
    box.appendChild(el('div', 'view-hint', o.namesChecked.toLocaleString() + ' names checked (types, methods and fields; compiler-generated names are skipped).'));
    root.appendChild(box);
}
