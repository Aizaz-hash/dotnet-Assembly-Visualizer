let rawAssemblyData = { classes: [], inheritance: { nodes: [], edges: [] }, methodGraph: { nodes: [], edges: [] }, deadlocks: [] };
let currentViewMode = 'inheritance';
let currentSelectedClass = null;
let currentSelectedMethod = null;
let currentSelectedDeadlockIndex = null;
let networkInstance = null;
let originalGroups = {};
let messageTimeout = null;

function showLoader(statusText) {
    const loader = document.getElementById('canvasLoaderOverlay');
    const txt = document.getElementById('loaderStatusText');
    if (loader && txt) { txt.textContent = statusText; loader.style.display = 'flex'; }
}

function toggleFolder(headerElement) {
    headerElement.parentElement.classList.toggle('collapsed');
}

function hideLoader() {
    const loader = document.getElementById('canvasLoaderOverlay');
    if (loader) loader.style.display = 'none';
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
}

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
        container.innerHTML = `<div style="padding:12px; color:#6c757d; text-align:center;">No executable methods found.</div>`;
        trigger.classList.add('disabled');
        return;
    }

    trigger.classList.remove('disabled');
    const filtered = methods.filter(m => m.name.toLowerCase().includes(query));

    filtered.sort((a, b) => a.name.localeCompare(b.name)).forEach(method => {
        const div = document.createElement('div');
        div.className = 'method-item';
        div.innerHTML = `<strong>${method.name}()</strong>`;
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
        let suggestions = [];
        sortedNamespaces.forEach(ns => {
            originalGroups[ns].forEach(item => {
                if (item.shortName.toLowerCase().includes(query) || item.fullName.toLowerCase().includes(query)) {
                    suggestions.push({ ...item, namespace: ns });
                }
            });
        });

        if (suggestions.length === 0) {
            treeContent.innerHTML = `<div style="padding:12px; color:#6c757d; text-align:center;">No structural matches found.</div>`;
            return;
        }

        suggestions.slice(0, 30).forEach(item => {
            const div = document.createElement('div');
            div.className = 'class-item';
            div.innerHTML = `<strong>${item.shortName}</strong><span style="font-size:0.7rem;color:#6c757d;">${item.namespace}</span>`;
            div.onclick = () => selectClassItem(item.fullName, item.shortName);
            treeContent.appendChild(div);
        });
        return;
    }

    sortedNamespaces.forEach(ns => {
        const items = originalGroups[ns];
        const group = document.createElement('div');
        group.className = 'folder-group collapsed';
        group.innerHTML = `
            <div class="folder-header" onclick="toggleFolder(this)">
                <span class="folder-toggle-icon">▼</span>
                <span>${ns || '(Global Namespace)'}</span>
            </div>
            <div class="folder-children"></div>
        `;
        const childrenContainer = group.querySelector('.folder-children');
        items.forEach(item => {
            const div = document.createElement('div');
            div.className = 'class-item';
            div.textContent = item.shortName;
            div.onclick = () => selectClassItem(item.fullName, item.shortName);
            childrenContainer.appendChild(div);
        });
        treeContent.appendChild(group);
    });
}

function renderGraphMap() {
    clearCanvas();
    const container = document.getElementById('mynetwork');

    if (currentViewMode !== 'deadlocks' && !currentSelectedClass) {
        container.innerHTML = `<div style="padding:40px;text-align:center;color:#6c757d;">Please choose a Target Type from the directory stream above.</div>`;
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
            id: n.id, label: n.name, shape: n.type === 'Interface' ? 'ellipse' : 'box',
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
            rawAssemblyData.methodGraph.nodes.filter(m => m.parentClass === currentSelectedClass).forEach(m => relevantMethods.add(m.id));
        }

        relevantMethods.forEach(id => {
            const localNode = rawAssemblyData.methodGraph.nodes.find(n => n.id === id);
            let methodName = localNode ? localNode.name : "";
            let fullClassName = methodToClassMap[id] || (localNode ? localNode.parentClass : "");

            if (!methodName || !fullClassName) {
                const cleanId = id.replace("()", "");
                const lastDot = cleanId.lastIndexOf('.');
                if (lastDot > -1) {
                    methodName = cleanId.substring(lastDot + 1);
                    fullClassName = cleanId.substring(0, lastDot);
                } else {
                    methodName = id;
                    fullClassName = "Unknown Class";
                }
            }

            const shortClassName = fullClassName.split('.').pop();

            nodes.push({
                id: id,
                label: `${shortClassName}\n➔ ${methodName}()`,
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
            container.innerHTML = `<div style="padding:40px;text-align:center;color:#6c757d;">Please select a detected deadlock cycle path trace option from the sidebar directory.</div>`;
            return;
        }

        const activeCyclePath = rawAssemblyData.deadlocks[currentSelectedDeadlockIndex];
        const nodesInCycle = new Set(activeCyclePath);

        nodesInCycle.forEach(id => {
            const localNode = rawAssemblyData.methodGraph.nodes.find(n => n.id === id);
            let methodName = localNode ? localNode.name : "";
            let fullClassName = localNode ? localNode.parentClass : "";

            if (!methodName) {
                const cleanId = id.replace("()", "");
                const lastDot = cleanId.lastIndexOf('.');
                methodName = lastDot > -1 ? cleanId.substring(lastDot + 1) : id;
                fullClassName = lastDot > -1 ? cleanId.substring(0, lastDot) : "External Reference";
            }

            const shortClassName = fullClassName.split('.').pop();

            nodes.push({
                id: id,
                label: `${shortClassName}\n➔ ${methodName}()`,
                shape: 'box',
                borderWidth: 2,
                color: {
                    background: '#ffebeb',
                    border: '#dc3545',
                    highlight: { background: '#ffcdd2', border: '#b71c1c' }
                },
                font: { multi: true, color: '#721c24' },
                title: `Deadlock Component: ${fullClassName}.${methodName}()`
            });
        });

        for (let i = 0; i < activeCyclePath.length - 1; i++) {
            edges.push({
                from: activeCyclePath[i],
                to: activeCyclePath[i + 1],
                arrows: 'to',
                color: { color: '#dc3545', highlight: '#b71c1c' },
                width: 3,
                label: 'causes lock',
                font: { color: '#b71c1c', size: 10, vadjust: -10 }
            });
        }
    }

    const options = {
        nodes: { font: { face: 'sans-serif', multi: true }, margin: 12 },
        edges: { selectionWidth: 2, font: { size: 11 } },
        physics: {
            solver: 'forceAtlas2Based',
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

    networkInstance = new vis.Network(container, { nodes, edges }, options);

    networkInstance.on("stabilizationIterationsDone", function () {
        networkInstance.setOptions({ physics: false });
    });
}

document.getElementById('dllFile').addEventListener('change', async (e) => {
    const file = e.target.files[0];
    if (!file) return;

    currentSelectedClass = null;
    currentSelectedMethod = null;
    currentSelectedDeadlockIndex = null;
    document.getElementById('triggerText').textContent = "-- Select Target Type --";
    document.getElementById('methodTriggerText').textContent = "-- Select Target Method --";

    document.getElementById('searchClass').value = "";
    document.getElementById('searchMethod').value = "";
    document.getElementById('currentDllName').textContent = file.name;

    showLoader('Parsing IL Assemblies...');
    const formData = new FormData();
    formData.append('file', file);

    try {
        const response = await fetch('/api/analysis/analyze', { method: 'POST', body: formData });
        if (!response.ok) throw new Error("Metadata generation failed.");
        rawAssemblyData = await response.json();

        const deadlocksFound = rawAssemblyData.deadlocks || [];
        document.getElementById('deadlockCount').innerText = deadlocksFound.length;

        const deadlockContainer = document.getElementById('deadlockList');
        deadlockContainer.innerHTML = '';

        if (deadlocksFound.length === 0) {
            deadlockContainer.innerHTML = '<div style="color: #28a745; font-size:0.85rem; font-weight:bold; padding: 10px; background: #e2f0d9; border-radius:4px;">✓ No deadlocks or circular recursion paths detected!</div>';
        } else {
            deadlocksFound.forEach((cycle, index) => {
                const btn = document.createElement('button');
                btn.style = "text-align: left; padding: 10px; background: #fff5f5; border: 1px solid #feb2b2; border-radius: 6px; cursor: pointer; font-size: 0.8rem; font-family: monospace; transition: all 0.2s; width: 100%; box-sizing: border-box;";

                const originNode = cycle[0].split('.').pop();
                const targetNode = cycle[cycle.length - 2].split('.').pop();

                btn.innerHTML = `<b style="color: #c53030;">[Loop #${index + 1}]</b> <br/> ${originNode} ➔ ... ➔ ${targetNode}`;

                btn.onclick = () => {
                    Array.from(deadlockContainer.children).forEach(child => child.style.background = '#fff5f5');
                    btn.style.background = '#fed7d7';
                    currentSelectedDeadlockIndex = index;
                    renderGraphMap();
                };
                deadlockContainer.appendChild(btn);
            });
        }

        originalGroups = {};
        rawAssemblyData.classes.forEach(fullName => {
            const parts = fullName.split('.');
            const shortName = parts.pop();
            const ns = parts.join('.');
            if (!originalGroups[ns]) originalGroups[ns] = [];
            originalGroups[ns].push({ fullName, shortName });
        });

        document.getElementById('dropdownTrigger').classList.remove('disabled');

        setTimeout(() => {
            renderTreeView();
            hideLoader();
            renderGraphMap();
            showMessage('Assembly structures parsed cleanly.', 'success');
        }, 50);

    } catch (err) {
        hideLoader();
        showMessage(err.message, 'error');
    }
});

document.getElementById('searchClass').addEventListener('input', (e) => renderTreeView(e.target.value));
document.getElementById('searchMethod').addEventListener('input', (e) => populateMethodsDropdown(e.target.value));
document.getElementById('dropdownTrigger').onclick = () => { document.getElementById('dropdownMenu').style.display = 'flex'; };
document.getElementById('methodTrigger').onclick = () => { document.getElementById('methodMenu').style.display = 'flex'; };

window.onclick = function (event) {
    const classWrapper = document.getElementById('classDropdownWrapper');
    const methodWrapper = document.getElementById('methodDropdownWrapper');

    if (classWrapper && !classWrapper.contains(event.target)) {
        document.getElementById('dropdownMenu').style.display = "none";
    }
    if (methodWrapper && !methodWrapper.contains(event.target)) {
        document.getElementById('methodMenu').style.display = "none";
    }
}