// C# Source tab: the decompiled C# of the selected type or method (ICSharpCode.Decompiler, the engine behind ILSpy).
// The assembly stays on this server for a few minutes after the first request so moving between methods is quick.
'use strict';

const codeSessions = new Map();       // assembly name -> { id, file }  (id from POST /api/decompile)
const codeCache = new Map();          // "name|token" -> source text
let sourceScope = 'auto';             // auto | method | type
let sourceRequest = 0;                // ignores answers that arrive after the selection changed

async function codeSessionFor(name, forceNew) {
    const file = loadedFiles.get(name);
    if (!file) throw new Error('The file of this assembly is no longer available - open it again.');
    const known = codeSessions.get(name);
    if (known && known.file === file && !forceNew) return known.id;

    const form = new FormData(); form.append('file', file);
    const response = await fetch('/api/decompile', { method: 'POST', body: form });
    if (!response.ok) throw new Error((await response.text()) || ('Server returned ' + response.status));
    const id = (await response.json()).id;
    codeSessions.set(name, { id, file });
    return id;
}

async function fetchSource(name, token) {
    const key = name + '|' + token;
    if (codeCache.has(key)) return codeCache.get(key);
    for (let attempt = 0; attempt < 2; attempt++) {
        const id = await codeSessionFor(name, attempt > 0);
        const response = await fetch('/api/decompile/' + id + '?token=' + token);
        if (response.status === 404 && attempt === 0) continue;              // expired on the server: upload again once
        if (!response.ok) throw new Error((await response.text()) || ('Server returned ' + response.status));
        const text = await response.text();
        if (codeCache.size > 200) codeCache.clear();
        codeCache.set(key, text);
        return text;
    }
    throw new Error('Could not open the assembly on the server.');
}

// What is selected: a method wins over its type unless the user chose "Type".
function sourceTarget() {
    const method = state.method ? methodById.get(state.method) : null;
    const type = state.type ? typeNodes.get(state.type) : (method ? typeNodes.get(method.parentClass) : null);
    if (sourceScope === 'type' && type) return { kind: 'type', label: type.id, token: type.token };
    if (method && sourceScope !== 'type') return { kind: 'method', label: method.parentClass + '.' + method.name, token: method.token };
    if (type) return { kind: 'type', label: type.id, token: type.token };
    return null;
}

function renderSource() {
    const root = byId('sourceScroll');
    root.innerHTML = '';
    if (!hasAssembly) { root.appendChild(el('div', 'empty-note', 'Open a .NET assembly, then pick a type or method in the explorer.')); return; }

    const target = sourceTarget();
    if (!target || target.token == null) {
        root.appendChild(el('div', 'empty-note', target ? 'This analysis has no source information (it was produced by an older version). Reload the assembly.' : 'Select a type or a method in the explorer to see its C#.'));
        return;
    }

    const bar = el('div', 'src-bar');
    bar.appendChild(el('span', 'src-label', target.label));
    if (state.method) {
        const seg = el('div', 'seg');
        [['auto', 'Method'], ['type', 'Whole type']].forEach(([key, text]) => {
            const b = el('button', 'seg-btn' + ((sourceScope === 'type') === (key === 'type') ? ' active' : ''), text);
            b.onclick = () => { sourceScope = key === 'type' ? 'type' : 'auto'; renderSource(); };
            seg.appendChild(b);
        });
        bar.appendChild(seg);
    }
    const copy = el('button', 'tb-btn small', 'Copy'); bar.appendChild(copy);
    root.appendChild(bar);

    const pre = el('pre', 'src-code'); pre.textContent = 'Decompiling…';
    root.appendChild(pre);
    root.appendChild(el('div', 'view-hint', 'Decompiled from the IL by ICSharpCode.Decompiler: it reads like the original C# but names, comments and some constructs cannot be recovered.'));

    const request = ++sourceRequest;
    const name = currentAssemblyName;
    fetchSource(name, target.token).then((text) => {
        if (request !== sourceRequest) return;
        pre.innerHTML = CSharpHighlight.highlight(text);
        copy.onclick = () => { navigator.clipboard && navigator.clipboard.writeText(text).then(() => { copy.textContent = 'Copied'; setTimeout(() => { copy.textContent = 'Copy'; }, 1200); }); };
    }).catch((err) => {
        if (request !== sourceRequest) return;
        pre.textContent = '// Could not get the source: ' + err.message;
    });
}

byId('btnSourceCalls').onclick = () => navigate({ view: 'source' });
