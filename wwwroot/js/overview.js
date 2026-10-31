// Overview: the first screen after a DLL is opened. Who the assembly is, a first look at what it contains and where to go next.
// The facts come from the same analysis result as every other view; nothing is computed or sent twice.
'use strict';

function overviewVulnState() {
    if (vulnReport) {
        const open = (vulnReport.findings || []).filter(f => vulnStatusOf(f) === 'Open');
        const urgent = open.filter(f => f.severity === 'Critical' || f.severity === 'High').length;
        return { state: 'done', open: open.length, urgent };
    }
    return vulnIsBusy() ? { state: 'scanning' } : { state: 'none' };
}

// Opens API Compatibility with this DLL already chosen as the old version, so only the other one is missing.
function compareWithAnother() {
    const file = loadedFiles.get(currentAssemblyName);
    if (file) { compareState.oldFile = file; compareState.newFile = null; compareState.result = null; }
    navigate({ view: 'compare' });
}

// A one-line hint that links to Compare Versions: helpful text, not a warning.
function compareNote(withAssembly) {
    const note = el('div', 'info-note');
    note.id = 'overviewCompareNote';
    note.appendChild(document.createTextNode(withAssembly ? 'Have another version of this assembly? Use ' : 'Have two versions of an assembly? Use '));
    const link = el('a', 'mn-link', 'Compare Versions');
    link.href = '#';
    link.title = 'See what changed in the public API and what can break callers';
    link.onclick = (e) => { e.preventDefault(); if (withAssembly) compareWithAnother(); else navigate({ view: 'compare' }); };
    note.appendChild(link);
    note.appendChild(document.createTextNode(' to see what changed in its public API and what can break callers of it.'));
    return note;
}

function renderOverview() {
    const root = byId('overviewScroll');
    root.innerHTML = '';
    root.appendChild(compareNote(hasAssembly));
    if (!hasAssembly) {
        root.appendChild(el('div', 'empty-note', 'Open a .NET assembly (.dll or .exe), a folder or a NuGet package to see what it is.'));
        return;
    }

    const a = rawAssemblyData.assembly;
    const head = el('div', 'ov-head');
    head.appendChild(el('div', 'ov-name', a ? a.name : currentAssemblyName));
    if (a && a.version) head.appendChild(el('span', 'ov-version', a.version));
    root.appendChild(head);

    if (a) {
        const grid = el('div', 'asm-grid ov-grid');
        OverviewModel.identityRows(a).forEach(([k, v, mono]) => { grid.appendChild(el('span', 'asm-k', k)); grid.appendChild(el('span', 'asm-v' + (mono ? ' mono' : ''), String(v))); });
        root.appendChild(grid);
    } else {
        root.appendChild(el('div', 'empty-note', 'This analysis has no assembly details (it was produced by an older version). Reload the assembly.'));
    }

    renderObfuscation(root, rawAssemblyData.obfuscation);

    root.appendChild(el('div', 'ov-section', 'At a glance'));
    const cards = el('div', 'risky-counts ov-cards');
    const methods = rawAssemblyData.methodGraph ? rawAssemblyData.methodGraph.nodes.length : 0;
    OverviewModel.glanceCards({
        types: typeNodes.size, methods, namespaces: new Set(nsOf.values()).size, cycles: cycles.length,
        risky: (rawAssemblyData.riskyApis || []).length, metrics: rawAssemblyData.metrics || null,
        unreferenced: (rawAssemblyData.deadCode || []).length, vuln: overviewVulnState()
    }).forEach(c => {
        const card = el('div', 'risky-count ov-card' + (c.tone ? ' ' + c.tone : ''));
        card.appendChild(el('div', 'rcn-num', String(c.num)));
        card.appendChild(el('div', 'rcn-label', c.label));
        card.appendChild(el('div', 'ov-hint', c.hint));
        card.title = c.hint;
        card.onclick = () => navigate({ view: c.view });
        cards.appendChild(card);
    });
    root.appendChild(cards);
}
