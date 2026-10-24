let rawAssemblyData = { classes: [], inheritance: { nodes: [], edges: [] }, methodGraph: { nodes: [], edges: [] }, deadlocks: [] };
let currentViewMode = 'inheritance';
let currentSelectedClass = null;
let currentSelectedMethod = null;
let currentSelectedDeadlockIndex = null;
let showAllMethods = false;
let networkInstance = null;
let originalGroups = {};
let messageTimeout = null;
let layoutTimeout = null;
let hasAssembly = false;

// Previously analysed assemblies of this session (name -> analysis result); lets the user switch without re-uploading.
const loadedAssemblies = new Map();
const MAX_LOADED_ASSEMBLIES = 5;
const LARGE_CLASS_METHOD_THRESHOLD = 60;
let maxUploadMb = null;

// ---------- helpers ----------

function el(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
}

function showLoader(statusText) {
    const loader = document.getElementById('canvasLoaderOverlay');
    const txt = document.getElementById('loaderStatusText');
    if (loader && txt) { txt.textContent = statusText; loader.style.display = 'flex'; }
}

function hideLoader() {
    const loader = document.getElementById('canvasLoaderOverlay');
    if (loader) loader.style.display = 'none';
    if (layoutTimeout) { clearTimeout(layoutTimeout); layoutTimeout = null; }
}

function toggleFolder(headerElement) {
    headerElement.parentElement.classList.toggle('collapsed');
}

function showMessage(text, type = 'info') {
    const win = document.getElementById('msgWindow');
    const txt = document.getElementById('msgText');
    if (!win || !txt) return;
    if (messageTimeout) clearTimeout(messageTimeout);
    win.className = '';
    win.classList.add(type === 'error' ? 'msg-error' : (type === 'success' ? 'msg-success' : 'msg-info'));
    win.style.display = 'flex';
    txt.textContent = text;
    if (type !== 'error') { messageTimeout = setTimeout(closeMessage, 5000); }
}

function closeMessage() {
    const win = document.getElementById('msgWindow');
    if (win) win.style.display = 'none';
}

function clearCanvas() {
    if (networkInstance !== null) { networkInstance.destroy(); networkInstance = null; }
    setGraphToolsVisible(false);
}

function setGraphToolsVisible(visible) {
    const tools = document.getElementById('graphTools');
    if (tools) tools.style.display = visible ? 'flex' : 'none';
}

function showCanvasMessage(text, buttonText, onButton) {
    const container = document.getElementById('mynetwork');
    container.innerHTML = '';
    const box = el('div', 'canvas-message');
    box.appendChild(el('div', 'canvas-message-text', text));
    if (buttonText) {
        const btn = el('button', 'canvas-message-btn', buttonText);
        btn.onclick = onButton;
        box.appendChild(btn);
    }
    container.appendChild(box);
}

// "ns.Class.Method(System.String)" -> { className: "ns.Class", methodName: "Method(System.String)" }
function splitMethodId(id) {
    const paren = id.indexOf('(');
    const head = paren > -1 ? id.substring(0, paren) : id;
    const tail = paren > -1 ? id.substring(paren) : '';
    const lastDot = head.lastIndexOf('.');
    if (lastDot < 0) return { className: '', methodName: id };
    return { className: head.substring(0, lastDot), methodName: head.substring(lastDot + 1) + tail };
}

function truncate(text, max) {
    return text.length > max ? text.substring(0, max - 1) + "…" : text;
}

function shortClass(fullName) {
    return fullName ? fullName.split('.').pop() : '';
}

// ---------- view switching ----------

function switchViewMode(mode) {
    currentViewMode = mode;
    document.getElementById('tabInheritance').classList.toggle('active', mode === 'inheritance');
    document.getElementById('tabMethods').classList.toggle('active', mode === 'methods');
    document.getElementById('tabDeadlocks').classList.toggle('active', mode === 'deadlocks');

    const classWrapper = document.getElementById('classDropdownWrapper');
    const methodWrapper = document.getElementById('methodDropdownWrapper');
    const legendMethodItem = document.getElementById('legendMethodItem');
    const legendDeadlockItem = document.getElementById('legendDeadlockItem');
    const deadlocksPanel = document.getElementById('deadlocksSidePanel');

    if (mode === 'methods') {
        classWrapper.style.display = 'inline-block';
        methodWrapper.style.display = 'inline-block';
        legendMethodItem.style.display = 'flex';
        legendDeadlockItem.style.display = 'none';
        deadlocksPanel.style.display = 'none';
        if (currentSelectedClass) populateMethodsDropdown();
    } else if (mode === 'deadlocks') {
        classWrapper.style.display = 'none';
        methodWrapper.style.display = 'none';
        legendMethodItem.style.display = 'none';
        legendDeadlockItem.style.display = 'flex';
        deadlocksPanel.style.display = 'flex';
    } else {
        classWrapper.style.display = 'inline-block';
        methodWrapper.style.display = 'none';
        legendMethodItem.style.display = 'none';
        legendDeadlockItem.style.display = 'none';
        deadlocksPanel.style.display = 'none';
    }
    renderGraphMap();
}

function selectClassItem(fullName, shortName) {
    currentSelectedClass = fullName;
    currentSelectedMethod = null;
    showAllMethods = false;
    document.getElementById('triggerText').textContent = shortName;
    document.getElementById('dropdownMenu').style.display = 'none';
    document.getElementById('methodTriggerText').textContent = "-- Select Target Method --";

    if (currentViewMode === 'methods') {
        populateMethodsDropdown();
    }
    renderGraphMap();
}

function selectMethodItem(methodId, methodName) {
    currentSelectedMethod = methodId;
    document.getElementById('methodTriggerText').textContent = methodName;
    document.getElementById('methodTriggerText').title = methodId;
    document.getElementById('methodMenu').style.display = 'none';
    renderGraphMap();
}

function populateMethodsDropdown(filterText = "") {
    const container = document.getElementById('methodTreeContent');
    const trigger = document.getElementById('methodTrigger');
    if (!container || !currentSelectedClass) return;

    container.innerHTML = "";
    const query = filterText.toLowerCase().trim();
    const methods = rawAssemblyData.methodGraph.nodes.filter(m => m.parentClass === currentSelectedClass);

    if (methods.length === 0) {
        container.appendChild(el('div', 'empty-note', 'No executable methods found.'));
        trigger.classList.add('disabled');
        return;
    }

    trigger.classList.remove('disabled');
    const filtered = methods.filter(m => m.name.toLowerCase().includes(query));

    filtered.sort((a, b) => a.name.localeCompare(b.name)).forEach(method => {
        const div = el('div', 'method-item');
        div.title = method.id;
        div.appendChild(el('strong', null, `${method.name}()`));
        div.onclick = () => selectMethodItem(method.id, method.name);
        container.appendChild(div);
    });
}

function renderTreeView(filterText = "") {
    const treeContent = document.getElementById('treeContent');
    if (!treeContent) return;
    treeContent.innerHTML = "";

    const query = filterText.toLowerCase().trim();
    const sortedNamespaces = Object.keys(originalGroups).sort();

    if (query !== "") {
        const suggestions = [];
        sortedNamespaces.forEach(ns => {
            originalGroups[ns].forEach(item => {
                if (item.shortName.toLowerCase().includes(query) || item.fullName.toLowerCase().includes(query)) {
                    suggestions.push({ ...item, namespace: ns });
                }
            });
        });

        if (suggestions.length === 0) {
            treeContent.appendChild(el('div', 'empty-note', 'No structural matches found.'));
            return;
        }

        suggestions.slice(0, 50).forEach(item => {
            const div = el('div', 'class-item');
            div.title = item.fullName;
            div.appendChild(el('strong', null, item.shortName));
            div.appendChild(el('span', 'class-item-ns', item.namespace || '(Global Namespace)'));
            div.onclick = () => selectClassItem(item.fullName, item.shortName);
            treeContent.appendChild(div);
        });
        if (suggestions.length > 50) {
            treeContent.appendChild(el('div', 'empty-note', `Showing first 50 of ${suggestions.length} matches - keep typing to narrow down.`));
        }
        return;
    }

    sortedNamespaces.forEach(ns => {
        const items = originalGroups[ns];
        const group = el('div', 'folder-group collapsed');

        const header = el('div', 'folder-header');
        header.onclick = () => toggleFolder(header);
        header.appendChild(el('span', 'folder-toggle-icon', '▼'));
        header.appendChild(el('span', 'folder-name', ns || '(Global Namespace)'));
        header.appendChild(el('span', 'folder-count', String(items.length)));
        header.title = ns || '(Global Namespace)';
        group.appendChild(header);

        const childrenContainer = el('div', 'folder-children');
        items.forEach(item => {
            const div = el('div', 'class-item', item.shortName);
            div.title = item.fullName;
            div.onclick = () => selectClassItem(item.fullName, item.shortName);
            childrenContainer.appendChild(div);
        });
        group.appendChild(childrenContainer);
        treeContent.appendChild(group);
    });
}

// ---------- graph rendering ----------

function methodNodeFor(id, methodToClassMap) {
    const localNode = rawAssemblyData.methodGraph.nodes.find(n => n.id === id);
    let methodName = localNode ? localNode.name : "";
    let fullClassName = (methodToClassMap && methodToClassMap[id]) || (localNode ? localNode.parentClass : "");

    if (!methodName || !fullClassName) {
        const parts = splitMethodId(id);
        methodName = parts.methodName.replace(/\(\)$/, '');
        fullClassName = parts.className || "External Reference";
    }
    return { localNode, methodName, fullClassName };
}

function renderGraphMap() {
    clearCanvas();
    hideLoader();
    const container = document.getElementById('mynetwork');
    container.innerHTML = "";

    if (!hasAssembly) {
        showCanvasMessage('Upload a .NET assembly to begin: click "Choose DLL" or drop a .dll file anywhere on this page.');
        return;
    }

    if (currentViewMode !== 'deadlocks' && !currentSelectedClass) {
        showCanvasMessage('Choose a Target Type from the dropdown above.');
        return;
    }

    let nodes = [], edges = [];

    if (currentViewMode === 'inheritance') {
        const connectedNodes = new Set([currentSelectedClass]);
        rawAssemblyData.inheritance.edges.forEach(e => {
            if (e.from === currentSelectedClass || e.to === currentSelectedClass) {
                connectedNodes.add(e.from); connectedNodes.add(e.to);
            }
        });

        nodes = rawAssemblyData.inheritance.nodes.filter(n => connectedNodes.has(n.id)).map(n => ({
            id: n.id, label: n.name, title: n.id, shape: n.type === 'Interface' ? 'ellipse' : 'box',
            color: n.type === 'Interface' ? '#e3f2fd' : '#fff3e0'
        }));

        edges = rawAssemblyData.inheritance.edges.filter(e => connectedNodes.has(e.from) && connectedNodes.has(e.to)).map(e => ({
            from: e.from,
            to: e.to,
            arrows: 'to',
            label: e.relation,
            length: 220,
            font: { align: 'horizontal', vadjust: -12, size: 11, color: '#495057' }
        }));

    } else if (currentViewMode === 'methods') {
        const relevantMethods = new Set();
        const methodToClassMap = {};

        rawAssemblyData.methodGraph.nodes.forEach(n => {
            methodToClassMap[n.id] = n.parentClass;
        });

        if (currentSelectedMethod) {
            relevantMethods.add(currentSelectedMethod);
            rawAssemblyData.methodGraph.edges.forEach(e => {
                if (e.from === currentSelectedMethod || e.to === currentSelectedMethod) {
                    relevantMethods.add(e.from); relevantMethods.add(e.to);
                    if (e.fromClass) methodToClassMap[e.from] = e.fromClass;
                    if (e.toClass) methodToClassMap[e.to] = e.toClass;
                }
            });
        } else {
            const classMethods = rawAssemblyData.methodGraph.nodes.filter(m => m.parentClass === currentSelectedClass);
            if (classMethods.length > LARGE_CLASS_METHOD_THRESHOLD && !showAllMethods) {
                showCanvasMessage(
                    `${shortClass(currentSelectedClass)} has ${classMethods.length} methods - drawing all of them would be unreadable. ` +
                    'Pick a method from "Select Target Method" to see its callers and callees.',
                    `Show all ${classMethods.length} methods anyway`,
                    () => { showAllMethods = true; renderGraphMap(); });
                return;
            }
            classMethods.forEach(m => relevantMethods.add(m.id));
        }

        relevantMethods.forEach(id => {
            const { localNode, methodName, fullClassName } = methodNodeFor(id, methodToClassMap);
            nodes.push({
                id: id,
                label: `${shortClass(fullClassName)}\n➔ ${truncate(methodName, 40)}()`,
                shape: 'box',
                color: localNode ? '#e8f5e9' : '#f5f5f5',
                title: `Full Path: ${fullClassName}.${methodName}()`,
                font: { multi: true, bold: { color: '#212529' } }
            });
        });

        edges = rawAssemblyData.methodGraph.edges.filter(e => relevantMethods.has(e.from) && relevantMethods.has(e.to)).map(e => ({
            from: e.from, to: e.to, arrows: 'to'
        }));

    } else if (currentViewMode === 'deadlocks') {
        if (currentSelectedDeadlockIndex === null) {
            showCanvasMessage(
                rawAssemblyData.deadlocks.length === 0
                    ? 'No circular call chains were detected in this assembly.'
                    : 'Select a circular call chain from the list on the right to draw it.');
            return;
        }

        const activeCyclePath = rawAssemblyData.deadlocks[currentSelectedDeadlockIndex];
        const nodesInCycle = new Set(activeCyclePath);

        nodesInCycle.forEach(id => {
            const { methodName, fullClassName } = methodNodeFor(id, null);
            nodes.push({
                id: id,
                label: `${shortClass(fullClassName)}\n➔ ${truncate(methodName, 40)}()`,
                shape: 'box',
                borderWidth: 2,
                color: {
                    background: '#ffebeb',
                    border: '#dc3545',
                    highlight: { background: '#ffcdd2', border: '#b71c1c' }
                },
                font: { multi: true, color: '#721c24' },
                title: `Part of a circular chain: ${fullClassName}.${methodName}()`
            });
        });

        for (let i = 0; i < activeCyclePath.length - 1; i++) {
            edges.push({
                from: activeCyclePath[i],
                to: activeCyclePath[i + 1],
                arrows: 'to',
                color: { color: '#dc3545', highlight: '#b71c1c' },
                width: 3,
                label: `${i + 1}`,
                font: { color: '#b71c1c', size: 11, vadjust: -10 }
            });
        }
    }

    const options = {
        nodes: { font: { face: 'sans-serif', multi: true }, margin: 12 },
        edges: { selectionWidth: 2, font: { size: 11 } },
        interaction: { tooltipDelay: 200, hover: true },
        physics: {
            solver: 'forceAtlas2Based',
            stabilization: { iterations: 250, fit: true },
            forceAtlas2Based: {
                gravitationalConstant: -80,
                centralGravity: 0.01,
                springLength: currentViewMode === 'methods' ? 180 : 120,
                springConstant: 0.08,
                damping: 0.4,
                avoidOverlap: 1.0
            }
        }
    };

    const needsLayoutWait = nodes.length > 1;
    if (needsLayoutWait) showLoader(`Laying out ${nodes.length} nodes...`);

    // Let the loader paint before the (potentially heavy) layout starts.
    setTimeout(() => {
        networkInstance = new vis.Network(container, { nodes, edges }, options);
        setGraphToolsVisible(true);

        const finish = () => {
            hideLoader();
            if (networkInstance) {
                networkInstance.setOptions({ physics: false });
                networkInstance.fit({ animation: false });
            }
        };
        networkInstance.once("stabilizationIterationsDone", finish);
        if (needsLayoutWait) layoutTimeout = setTimeout(finish, 10000);
    }, 30);
}

function fitGraph() {
    if (networkInstance) networkInstance.fit({ animation: { duration: 300 } });
}

function exportGraphPng() {
    if (!networkInstance) { showMessage('Nothing to export yet - draw a graph first.', 'info'); return; }
    const canvas = document.querySelector('#mynetwork canvas');
    if (!canvas) return;
    const out = document.createElement('canvas');
    out.width = canvas.width; out.height = canvas.height;
    const ctx = out.getContext('2d');
    ctx.fillStyle = '#ffffff';
    ctx.fillRect(0, 0, out.width, out.height);
    ctx.drawImage(canvas, 0, 0);
    const a = document.createElement('a');
    a.href = out.toDataURL('image/png');
    a.download = `${currentViewMode}-graph.png`;
    a.click();
}

// ---------- circular chain list ----------

function renderCycleList() {
    const found = rawAssemblyData.deadlocks || [];
    document.getElementById('deadlockCount').textContent = found.length;

    const list = document.getElementById('deadlockList');
    list.innerHTML = '';

    if (found.length === 0) {
        list.appendChild(el('div', 'cycle-none', '✓ No circular call chains detected.'));
        return;
    }

    found.forEach((cycle, index) => {
        const steps = cycle.length - 1;
        const first = splitMethodId(cycle[0]);
        const last = splitMethodId(cycle[cycle.length - 2] || cycle[0]);

        const btn = el('button', 'cycle-btn');
        btn.title = cycle.join('\n➔ ');

        const head = el('div', 'cycle-head');
        head.appendChild(el('b', null, `Loop #${index + 1}`));
        head.appendChild(el('span', 'cycle-len', `${steps} call${steps === 1 ? '' : 's'}`));
        btn.appendChild(head);

        const classLine = first.className === last.className
            ? shortClass(first.className)
            : `${shortClass(first.className)} … ${shortClass(last.className)}`;
        btn.appendChild(el('div', 'cycle-class', classLine));
        btn.appendChild(el('div', 'cycle-path', `${first.methodName} ➔ … ➔ ${last.methodName}`));

        btn.onclick = () => {
            Array.from(list.children).forEach(child => child.classList.remove('selected'));
            btn.classList.add('selected');
            currentSelectedDeadlockIndex = index;
            renderGraphMap();
        };
        list.appendChild(btn);
    });
}

// ---------- assembly loading ----------

function updateAssemblySwitcher(activeName) {
    const select = document.getElementById('assemblySwitcher');
    if (!select) return;
    select.innerHTML = '';
    loadedAssemblies.forEach((_, name) => {
        const opt = el('option', null, name);
        opt.value = name;
        select.appendChild(opt);
    });
    select.value = activeName;
    select.style.display = loadedAssemblies.size > 1 ? 'inline-block' : 'none';
}

function activateAssembly(name) {
    rawAssemblyData = loadedAssemblies.get(name);
    hasAssembly = true;

    currentSelectedClass = null;
    currentSelectedMethod = null;
    currentSelectedDeadlockIndex = null;
    showAllMethods = false;
    document.getElementById('triggerText').textContent = "-- Select Target Type --";
    document.getElementById('methodTriggerText').textContent = "-- Select Target Method --";
    document.getElementById('searchClass').value = "";
    document.getElementById('searchMethod').value = "";
    document.getElementById('currentDllName').textContent = name;
    document.getElementById('currentDllName').title = name;

    renderCycleList();

    originalGroups = {};
    rawAssemblyData.classes.forEach(fullName => {
        const parts = fullName.split('.');
        const shortName = parts.pop();
        const ns = parts.join('.').trim();
        if (!originalGroups[ns]) originalGroups[ns] = [];
        originalGroups[ns].push({ fullName, shortName });
    });

    document.getElementById('dropdownTrigger').classList.remove('disabled');
    renderTreeView();
    updateAssemblySwitcher(name);
    renderGraphMap();
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

async function loadFile(file) {
    if (!file) return;

    if (!/\.(dll|exe)$/i.test(file.name)) {
        showMessage(`"${file.name}" is not a .dll or .exe file.`, 'error');
        return;
    }
    if (maxUploadMb && file.size > maxUploadMb * 1024 * 1024) {
        showMessage(`"${file.name}" is ${(file.size / 1048576).toFixed(1)} MB; the upload limit is ${maxUploadMb} MB.`, 'error');
        return;
    }

    showLoader(`Parsing ${file.name}...`);
    const formData = new FormData();
    formData.append('file', file);

    try {
        const response = await fetch('/api/analysis/analyze', { method: 'POST', body: formData });
        if (!response.ok) throw new Error(await readErrorMessage(response));
        const data = await response.json();

        loadedAssemblies.delete(file.name);
        loadedAssemblies.set(file.name, data);
        while (loadedAssemblies.size > MAX_LOADED_ASSEMBLIES) {
            loadedAssemblies.delete(loadedAssemblies.keys().next().value);
        }

        hideLoader();
        activateAssembly(file.name);
        showMessage(`Parsed ${data.classes.length} types, ${data.methodGraph.nodes.length} methods, ${(data.deadlocks || []).length} circular chains.`, 'success');
    } catch (err) {
        hideLoader();
        const reason = err instanceof TypeError
            ? `the connection was closed - the file may exceed the server's upload limit${maxUploadMb ? ` (${maxUploadMb} MB)` : ''}.`
            : err.message;
        showMessage(`Could not analyse "${file.name}": ${reason}`, 'error');
    }
}

// ---------- wiring ----------

document.getElementById('dllFile').addEventListener('change', async (e) => {
    const file = e.target.files[0];
    await loadFile(file);
    e.target.value = '';   // allow re-selecting the same file
});

document.getElementById('assemblySwitcher').addEventListener('change', (e) => {
    if (loadedAssemblies.has(e.target.value)) activateAssembly(e.target.value);
});

document.getElementById('searchClass').addEventListener('input', (e) => renderTreeView(e.target.value));
document.getElementById('searchMethod').addEventListener('input', (e) => populateMethodsDropdown(e.target.value));
document.getElementById('dropdownTrigger').onclick = () => {
    if (document.getElementById('dropdownTrigger').classList.contains('disabled')) return;
    document.getElementById('dropdownMenu').style.display = 'flex';
    document.getElementById('searchClass').focus();
};
document.getElementById('methodTrigger').onclick = () => {
    if (document.getElementById('methodTrigger').classList.contains('disabled')) return;
    document.getElementById('methodMenu').style.display = 'flex';
    document.getElementById('searchMethod').focus();
};

window.onclick = function (event) {
    const classWrapper = document.getElementById('classDropdownWrapper');
    const methodWrapper = document.getElementById('methodDropdownWrapper');

    if (classWrapper && !classWrapper.contains(event.target)) {
        document.getElementById('dropdownMenu').style.display = "none";
    }
    if (methodWrapper && !methodWrapper.contains(event.target)) {
        document.getElementById('methodMenu').style.display = "none";
    }
};

// Drag & drop a DLL anywhere on the page.
let dragDepth = 0;
window.addEventListener('dragenter', (e) => {
    if (!e.dataTransfer || !Array.from(e.dataTransfer.types).includes('Files')) return;
    dragDepth++;
    document.body.classList.add('dragging');
});
window.addEventListener('dragleave', () => {
    dragDepth = Math.max(0, dragDepth - 1);
    if (dragDepth === 0) document.body.classList.remove('dragging');
});
window.addEventListener('dragover', (e) => { e.preventDefault(); });
window.addEventListener('drop', async (e) => {
    e.preventDefault();
    dragDepth = 0;
    document.body.classList.remove('dragging');
    const file = e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files[0];
    await loadFile(file);
});

// Show the real upload limit next to the upload button.
fetch('/api/analysis/limits').then(r => r.ok ? r.json() : null).then(info => {
    if (!info) return;
    maxUploadMb = info.maxFileSizeMB;
    const hint = document.getElementById('uploadHint');
    if (hint) hint.textContent = `.dll / .exe, max ${maxUploadMb} MB`;
}).catch(() => { /* hint is optional */ });

renderGraphMap();
