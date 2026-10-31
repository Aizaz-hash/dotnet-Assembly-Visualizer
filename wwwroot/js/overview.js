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
function compareNote() {
    const note = el('div', 'info-note');
    note.id = 'overviewCompareNote';
    note.appendChild(document.createTextNode('Have another version of this assembly? Use '));
    const link = el('a', 'mn-link', 'Compare Versions');
    link.href = '#';
    link.title = 'See what changed in the public API and what can break callers';
    link.onclick = (e) => { e.preventDefault(); compareWithAnother(); };
    note.appendChild(link);
    note.appendChild(document.createTextNode(' to see what changed in its public API and what can break callers of it.'));
    return note;
}

// What the page shows before anything is open: what the tool does and three ways to start (none of them needs an assembly first).
function renderOverviewIntro(root) {
    const intro = el('div', 'ov-intro');
    intro.appendChild(el('div', 'ov-intro-title', 'Understand a .NET assembly without running it'));
    intro.appendChild(el('p', 'ov-intro-text', 'Open a .dll or .exe - or a folder, a NuGet package or a zip - and the tool reads its metadata and IL: what the assembly is, how its parts depend on each other, who calls what, what looks risky and which dependencies have known vulnerabilities. Nothing is executed and your files are not kept.'));

    const starts = [
        { key: 'open', title: 'Open an assembly', text: 'Click Open, or drop a .dll / .exe, a folder or a .nupkg onto this page. It starts at this Overview.', cta: 'Open an assembly…', go: () => byId('dllFile').click() },
        { key: 'scan', title: 'Scan for known vulnerabilities', text: 'Check NuGet dependencies against OSV.dev - for an opened assembly, or on its own from a .deps.json, project.assets.json or packages.lock.json.', cta: 'Open Vulnerabilities', go: () => navigate({ view: 'vulns' }) },
        { key: 'compare', title: 'Compare two versions', text: 'Pick two versions of a DLL to list what changed in the public API and what can break the code that uses it.', cta: 'Open Compare Versions', go: () => navigate({ view: 'compare' }) }
    ];
    const cards = el('div', 'ov-start');
    starts.forEach(c => {
        const card = el('div', 'ov-start-card');
        card.id = 'ovStart' + c.key.charAt(0).toUpperCase() + c.key.slice(1);
        card.appendChild(el('div', 'ov-start-title', c.title));
        card.appendChild(el('div', 'ov-start-text', c.text));
        const b = el('button', 'tb-btn small', c.cta);
        b.onclick = c.go;
        card.appendChild(b);
        cards.appendChild(card);
    });
    intro.appendChild(cards);

    intro.appendChild(el('div', 'ov-section', 'What you get once an assembly is open'));
    const ul = el('ul', 'ov-list');
    [
        ['Overview', 'identity, framework and signing, the namespace architecture and the code metrics'],
        ['Details', 'pick a class for its inheritance, or a method for its callers and callees - each with its C# code - and every circular call chain'],
        ['Security & risk', 'risky API calls to review, and known vulnerabilities in the dependencies (scanned in the background)']
    ].forEach(([h, t]) => { const li = el('li'); li.appendChild(el('strong', null, h + ': ')); li.appendChild(document.createTextNode(t)); ul.appendChild(li); });
    intro.appendChild(ul);
    root.appendChild(intro);
}

function renderOverview() {
    const root = byId('overviewScroll');
    root.innerHTML = '';
    if (!hasAssembly) { renderOverviewIntro(root); return; }
    root.appendChild(compareNote());

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
