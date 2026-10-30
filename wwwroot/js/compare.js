// Compare tab: the public API of two versions of an assembly, with breaking changes called out.
// Both files go to this server only to read their metadata; nothing is stored.
'use strict';

const compareState = { oldFile: null, newFile: null, result: null, severity: '', text: '', busy: false };

function compareReady() { return !!(compareState.oldFile && compareState.newFile) && !compareState.busy; }

function renderCompare() {
    const root = byId('compareScroll');
    root.innerHTML = '';

    root.appendChild(el('p', 'risky-intro', 'Pick two versions of the same assembly to list what changed in its public API and which changes can break code that uses it. Only the metadata is read (nothing runs), so behaviour changes are not visible here.'));

    const pick = el('div', 'cmp-pick');
    [['oldFile', 'Old version'], ['newFile', 'New version']].forEach(([key, label]) => {
        const box = el('div', 'cmp-side');
        box.appendChild(el('div', 'cmp-label', label));
        box.appendChild(el('div', 'cmp-file' + (compareState[key] ? ' set' : ''), compareState[key] ? compareState[key].name + ' (' + Math.round(compareState[key].size / 1024) + ' KB)' : 'no file chosen'));
        const input = el('input'); input.type = 'file'; input.accept = '.dll,.exe'; input.style.display = 'none';
        input.onchange = () => { if (input.files[0]) { compareState[key] = input.files[0]; compareState.result = null; renderCompare(); } };
        const choose = el('button', 'tb-btn small', 'Choose file…'); choose.onclick = () => input.click();
        box.appendChild(input); box.appendChild(choose);
        const open = hasAssembly && loadedFiles.get(currentAssemblyName);
        if (open) {
            const use = el('button', 'tb-btn small', 'Use ' + currentAssemblyName);
            use.title = 'Use the assembly that is open in the explorer';
            use.onclick = () => { compareState[key] = open; compareState.result = null; renderCompare(); };
            box.appendChild(use);
        }
        pick.appendChild(box);
    });
    const go = el('button', 'btn-primary', compareState.busy ? 'Comparing…' : 'Compare');
    go.disabled = !compareReady(); go.onclick = runCompare;
    pick.appendChild(go);
    root.appendChild(pick);

    if (compareState.result) renderCompareResult(root, compareState.result);
}

async function runCompare() {
    if (!compareReady()) return;
    compareState.busy = true; renderCompare();
    const form = new FormData();
    form.append('oldFile', compareState.oldFile); form.append('newFile', compareState.newFile);
    try {
        const response = await fetch('/api/analysis/diff', { method: 'POST', body: form });
        if (!response.ok) throw new Error((await response.text()) || ('Server returned ' + response.status));
        compareState.result = await response.json();
        compareState.severity = ''; compareState.text = '';
    } catch (err) {
        compareState.result = null;
        showMessage('Compare failed: ' + err.message, 'error');
    } finally {
        compareState.busy = false; renderCompare();
    }
}

const CMP_BUMP = {
    major: 'Breaking changes: a major version bump (or a clear migration note) is called for.',
    minor: 'Only additions: a minor version bump is enough.',
    patch: 'No public API change: a patch bump is enough.'
};

function renderCompareResult(root, r) {
    const banner = el('div', 'vuln-banner ' + (r.breaking ? 'bad' : r.compatible ? 'warn' : 'ok'));
    banner.appendChild(el('div', 'vb-title', (r.breaking ? '✕ ' + r.breaking + ' breaking change' + (r.breaking === 1 ? '' : 's') : r.compatible ? '✓ No breaking changes' : '✓ The public API is identical')
        + ' · ' + r.oldName + ' ' + r.oldVersion + ' → ' + r.newName + ' ' + r.newVersion));
    banner.appendChild(el('div', null, CMP_BUMP[r.suggestedBump] + ' (' + r.oldTypes + ' → ' + r.newTypes + ' public types)'));
    root.appendChild(banner);

    const counts = el('div', 'risky-counts');
    [['breaking', r.breaking, 'Breaking', 'sev-high'], ['compatible', r.compatible, 'Compatible', 'sev-low'], ['info', r.info, 'Notes', 'sev-medium']].forEach(([key, n, label, cls]) => {
        const c = el('div', 'risky-count ' + cls + (compareState.severity === key ? ' active' : ''));
        c.appendChild(el('div', 'rcn-num', String(n))); c.appendChild(el('div', 'rcn-label', label));
        c.onclick = () => { compareState.severity = compareState.severity === key ? '' : key; renderCompare(); };
        counts.appendChild(c);
    });
    root.appendChild(counts);

    const bar = el('div', 'risky-filter');
    const search = el('input'); search.type = 'search'; search.placeholder = 'Filter by type or member…'; search.value = compareState.text;
    bar.appendChild(search);
    const md = el('button', 'tb-btn small', 'Export Markdown'); md.style.marginLeft = '10px'; md.onclick = () => downloadText(compareMarkdown(r), 'api-changes.md', 'text/markdown');
    const js = el('button', 'tb-btn small', 'Export JSON'); js.style.marginLeft = '6px'; js.onclick = () => downloadText(JSON.stringify(r, null, 2), 'api-changes.json', 'application/json');
    bar.appendChild(md); bar.appendChild(js);
    root.appendChild(bar);

    const host = el('div'); host.id = 'compareItems'; root.appendChild(host);
    const draw = () => {
        host.innerHTML = '';
        const q = compareState.text.trim().toLowerCase();
        const shown = r.changes.filter(c => (!compareState.severity || c.severity === compareState.severity) &&
            (!q || (c.type + ' ' + (c.member || '') + ' ' + c.message).toLowerCase().includes(q)));
        if (!shown.length) { host.appendChild(el('div', 'empty-note', r.changes.length ? 'Nothing matches the filter.' : 'No differences.')); return; }
        const byType = new Map();
        shown.forEach(c => { if (!byType.has(c.type)) byType.set(c.type, []); byType.get(c.type).push(c); });
        byType.forEach((list, type) => {
            const det = el('details', 'cmp-type'); det.open = byType.size <= 12;
            const worst = list.some(c => c.severity === 'breaking') ? 'breaking' : list.some(c => c.severity === 'compatible') ? 'compatible' : 'info';
            const sum = el('summary'); sum.appendChild(el('span', 'cmp-dot cmp-' + worst)); sum.appendChild(document.createTextNode(' ' + type + '  '));
            sum.appendChild(el('span', 'cmp-count', list.length + ' change' + (list.length === 1 ? '' : 's'))); det.appendChild(sum);
            list.forEach(c => {
                const row = el('div', 'cmp-row');
                row.appendChild(el('span', 'cmp-sev cmp-' + c.severity, c.severity === 'info' ? 'note' : c.severity));
                row.appendChild(el('span', 'cmp-msg', c.message));
                if (c.before || c.after) {
                    const d = el('div', 'cmp-diff');
                    if (c.before) d.appendChild(el('div', 'cmp-before', '- ' + c.before));
                    if (c.after) d.appendChild(el('div', 'cmp-after', '+ ' + c.after));
                    row.appendChild(d);
                }
                det.appendChild(row);
            });
            host.appendChild(det);
        });
    };
    search.oninput = () => { compareState.text = search.value; draw(); };
    draw();
}

function compareMarkdown(r) {
    const lines = [`# API changes: ${r.oldName} ${r.oldVersion} → ${r.newName} ${r.newVersion}`, '',
        `${r.breaking} breaking, ${r.compatible} compatible, ${r.info} notes. Suggested version bump: **${r.suggestedBump}**.`, ''];
    ['breaking', 'compatible', 'info'].forEach(sev => {
        const list = r.changes.filter(c => c.severity === sev);
        if (!list.length) return;
        lines.push(`## ${sev === 'info' ? 'Notes' : sev[0].toUpperCase() + sev.slice(1)} (${list.length})`, '');
        list.forEach(c => lines.push(`- \`${c.type}\`${c.member ? ' `' + c.member + '`' : ''}: ${c.message}`));
        lines.push('');
    });
    return lines.join('\n');
}

function downloadText(text, name, type) {
    const url = URL.createObjectURL(new Blob([text], { type }));
    const a = document.createElement('a'); a.href = url; a.download = name; a.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}
