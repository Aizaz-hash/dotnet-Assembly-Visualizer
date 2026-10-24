    // SESSION STATE STORAGE
    const loadedAssemblies = new Map(); // Key: fileName -> Value: { data: parsedJson, fileRef: File }
    let activeAssemblyFileName = null;

    let rawAssemblyData = {
        classes: [],
        inheritance: { nodes: [], edges: [] },
        methodGraph: { nodes: [], edges: [] },
        deadlocks: []
    };

    let currentViewMode = 'inheritance';
    let currentSelectedClass = null;
    let currentSelectedMethod = null;
    let currentSelectedDeadlockIndex = null;
    let networkInstance = null;
    let originalGroups = {};
    let messageTimeout = null;

    // UPLOAD & DUPLICATE OVERWRITE HANDLER
    document.getElementById('dllFile').addEventListener('change', async e => {
        const file = e.target.files[0];
        if (!file) return;

        await processAndSaveAssembly(file);
        e.target.value = ''; // Reset input to allow re-uploading the same file name
    });

    async function processAndSaveAssembly(file) {
        const isReplacement = loadedAssemblies.has(file.name);
        showLoader(isReplacement ? `Replacing ${file.name}...` : `Parsing ${file.name}...`);

        const formData = new FormData();
        formData.append('file', file);

        try {
            const response = await fetch('/api/analysis/analyze', {
                method: 'POST',
                body: formData
            });

            if (!response.ok) {
                throw new Error('Metadata generation failed.');
            }

            const parsedData = await response.json();

            // Save or Overwrite in active session
            loadedAssemblies.set(file.name, {
                data: parsedData,
                fileRef: file
            });

            activeAssemblyFileName = file.name;
            rawAssemblyData = parsedData;

            // Reset target selections
            currentSelectedClass = null;
            currentSelectedMethod = null;
            currentSelectedDeadlockIndex = null;
            document.getElementById('triggerText').textContent = '-- Select Target Type --';
            document.getElementById('methodTriggerText').textContent = '-- Select Target Method --';

            updateAssemblyUI();
            processAssemblyData();

            showMessage(
                isReplacement ? `Successfully replaced "${file.name}".` : `Loaded "${file.name}".`,
                'success'
            );

        } catch (err) {
            showMessage(err.message, 'error');
        } finally {
            hideLoader();
        }
    }

    // UPDATE ASSEMBLY DROPDOWN AND ACTION BUTTONS
    function updateAssemblyUI() {
        const container = document.getElementById('assemblyListContent');
        const triggerText = document.getElementById('assemblyTriggerText');
        const trigger = document.getElementById('assemblyTrigger');
        const btnRefresh = document.getElementById('btnRefresh');
        const btnRemove = document.getElementById('btnRemoveCurrent');

        if (!container) return;

        container.innerHTML = '';

        if (loadedAssemblies.size === 0) {
            triggerText.textContent = '-- No Assembly Loaded --';
            trigger.classList.add('disabled');
            btnRefresh.style.display = 'none';
            btnRemove.style.display = 'none';
            return;
        }

        trigger.classList.remove('disabled');
        btnRefresh.style.display = 'inline-block';
        btnRemove.style.display = 'inline-block';

        if (activeAssemblyFileName) {
            triggerText.textContent = activeAssemblyFileName;
        }

        // Populate menu
        loadedAssemblies.forEach((value, fileName) => {
            const div = document.createElement('div');
            div.className = 'class-item';
            div.style.fontWeight = fileName === activeAssemblyFileName ? 'bold' : 'normal';
            div.innerHTML = `📁 ${fileName}`;

            div.onclick = () => {
                if (activeAssemblyFileName !== fileName) {
                    activeAssemblyFileName = fileName;
                    rawAssemblyData = value.data;

                    currentSelectedClass = null;
                    currentSelectedMethod = null;
                    currentSelectedDeadlockIndex = null;
                    document.getElementById('triggerText').textContent = '-- Select Target Type --';
                    document.getElementById('methodTriggerText').textContent = '-- Select Target Method --';

                    updateAssemblyUI();
                    processAssemblyData();
                }
                document.getElementById('assemblyMenu').style.display = 'none';
            };

            container.appendChild(div);
        });
    }

    // REMOVE ACTIVE ASSEMBLY
    document.getElementById('btnRemoveCurrent').addEventListener('click', () => {
        if (!activeAssemblyFileName || !loadedAssemblies.has(activeAssemblyFileName)) return;

        const removedFileName = activeAssemblyFileName;
        loadedAssemblies.delete(removedFileName);

        if (loadedAssemblies.size > 0) {
            activeAssemblyFileName = loadedAssemblies.keys().next().value;
            rawAssemblyData = loadedAssemblies.get(activeAssemblyFileName).data;
        } else {
            activeAssemblyFileName = null;
            rawAssemblyData = {
                classes: [],
                inheritance: { nodes: [], edges: [] },
                methodGraph: { nodes: [], edges: [] },
                deadlocks: []
            };
        }

        currentSelectedClass = null;
        currentSelectedMethod = null;
        currentSelectedDeadlockIndex = null;
        document.getElementById('triggerText').textContent = '-- Select Target Type --';
        document.getElementById('methodTriggerText').textContent = '-- Select Target Method --';

        updateAssemblyUI();
        processAssemblyData();

        showMessage(`Removed explicitly: ${removedFileName}`, 'info');
    });

    // REFRESH ACTIVE ASSEMBLY
    document.getElementById('btnRefresh').addEventListener('click', async () => {
        if (!activeAssemblyFileName || !loadedAssemblies.has(activeAssemblyFileName)) {
            showMessage('No active assembly selected to refresh.', 'info');
            return;
        }

        const currentItem = loadedAssemblies.get(activeAssemblyFileName);

        if (currentItem && currentItem.fileRef) {
            await processAndSaveAssembly(currentItem.fileRef);
        } else {
            processAssemblyData();
            showMessage(`Refreshed view for ${activeAssemblyFileName}`, 'success');
        }
    });

    // CORE DATA WRAPPER
    function processAssemblyData() {
        // Populate deadlocks sidebar
        const deadlocksFound = rawAssemblyData.deadlocks || [];
        document.getElementById('deadlockCount').innerText = deadlocksFound.length;

        const deadlockContainer = document.getElementById('deadlockList');
        deadlockContainer.innerHTML = '';

        if (deadlocksFound.length === 0) {
            deadlockContainer.innerHTML = `
                <div style="color:#28a745; font-size:0.85rem; font-weight:bold; padding:10px; background:#e2f0d9; border-radius:4px;">
                    ✓ No circular dependencies detected!
                </div>`;
        } else {
            deadlocksFound.forEach((cycle, index) => {
                const btn = document.createElement('button');
                btn.style = `text-align:left; padding:10px; background:#fff5f5; border:1px solid #feb2b2; border-radius:6px; cursor:pointer; font-size:0.8rem; font-family:monospace; transition:all 0.2s; width:100%; box-sizing:border-box;`;

                const originNode = cycle[0].split('.').pop();
                const targetNode = cycle[cycle.length - 2].split('.').pop();

                btn.innerHTML = `
                    <b style="color:#c53030;">[Cycle #${index + 1}]</b><br/>
                    <span style="color:#8b0000; font-weight:bold;">START:</span> ${originNode}<br/>
                    ${originNode} ➔ ... ➔ ${targetNode} ➔ START`;

                btn.onclick = () => {
                    Array.from(deadlockContainer.children).forEach(child => child.style.background = '#fff5f5');
                    btn.style.background = '#fed7d7';
                    currentSelectedDeadlockIndex = index;
                    renderGraphMap();
                };

                deadlockContainer.appendChild(btn);
            });
        }

        // Build Class Groups
        originalGroups = {};
        rawAssemblyData.classes.forEach(fullName => {
            const parts = fullName.split('.');
            const shortName = parts.pop();
            const ns = parts.join('.');

            if (!originalGroups[ns]) {
                originalGroups[ns] = [];
            }

            originalGroups[ns].push({ fullName, shortName });
        });

        if (rawAssemblyData.classes.length > 0) {
            document.getElementById('dropdownTrigger').classList.remove('disabled');
        } else {
            document.getElementById('dropdownTrigger').classList.add('disabled');
        }

        renderTreeView();
        renderGraphMap();
    }

    // LOADER
    function showLoader(statusText) {
        const loader = document.getElementById('canvasLoaderOverlay');
        const txt = document.getElementById('loaderStatusText');

        if (loader && txt) {
            txt.textContent = statusText;
            loader.style.display = 'flex';
        }
    }

    function hideLoader() {
        const loader = document.getElementById('canvasLoaderOverlay');
        if (loader) {
            loader.style.display = 'none';
        }
    }

    // FOLDER TOGGLE
    function toggleFolder(headerElement) {
        headerElement.parentElement.classList.toggle('collapsed');
    }

    // MESSAGE UI
    function showMessage(text, type = 'info') {
        const win = document.getElementById('msgWindow');
        const txt = document.getElementById('msgText');

        if (!win || !txt) return;

        if (messageTimeout) {
            clearTimeout(messageTimeout);
        }

        win.className = '';
        win.classList.add(
            type === 'error' ? 'msg-error' : type === 'success' ? 'msg-success' : 'msg-info'
        );

        win.style.display = 'flex';
        txt.textContent = text;

        if (type !== 'error') {
            messageTimeout = setTimeout(closeMessage, 5000);
        }
    }

    function closeMessage() {
        const win = document.getElementById('msgWindow');
        if (win) {
            win.style.display = 'none';
        }
    }

    // CLEAR CANVAS
    function clearCanvas() {
        if (networkInstance !== null) {
            networkInstance.destroy();
            networkInstance = null;
        }
    }

    // VIEW MODE SWITCHING
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

            if (currentSelectedClass) {
                populateMethodsDropdown();
            }
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

    // CLASS SELECTION
    function selectClassItem(fullName, shortName) {
        currentSelectedClass = fullName;
        currentSelectedMethod = null;

        document.getElementById('triggerText').textContent = shortName;
        document.getElementById('dropdownMenu').style.display = 'none';
        document.getElementById('methodTriggerText').textContent = '-- Select Target Method --';

        if (currentViewMode === 'methods') {
            populateMethodsDropdown();
        }

        renderGraphMap();
    }

    // METHOD SELECTION
    function selectMethodItem(methodId, methodName) {
        currentSelectedMethod = methodId;
        document.getElementById('methodTriggerText').textContent = methodName;
        document.getElementById('methodMenu').style.display = 'none';

        renderGraphMap();
    }

    // METHOD DROPDOWN POPULATION
    function populateMethodsDropdown(filterText = "") {
        const container = document.getElementById('methodTreeContent');
        const trigger = document.getElementById('methodTrigger');

        if (!container || !currentSelectedClass) return;

        container.innerHTML = '';
        const query = filterText.toLowerCase().trim();

        const methods = rawAssemblyData.methodGraph.nodes.filter(
            m => m.parentClass === currentSelectedClass
        );

        if (methods.length === 0) {
            container.innerHTML = `
                    <div style="padding:12px; color:#6c757d; text-align:center;">
                        No executable methods found.
                    </div>`;
            trigger.classList.add('disabled');
            return;
        }

        trigger.classList.remove('disabled');

        const filtered = methods.filter(m => m.name.toLowerCase().includes(query));

        filtered.sort((a, b) => a.name.localeCompare(b.name)).forEach(method => {
            const div = document.createElement('div');
            div.className = 'method-item';
            div.innerHTML = `<strong>${method.name}</strong>`;
            div.onclick = () => selectMethodItem(method.id, method.name);
            container.appendChild(div);
        });
    }

    // CLASS TREE RENDER
    function renderTreeView(filterText = "") {
        const treeContent = document.getElementById('treeContent');
        if (!treeContent) return;

        treeContent.innerHTML = '';
        const query = filterText.toLowerCase().trim();
        const sortedNamespaces = Object.keys(originalGroups).sort();

        if (query !== '') {
            let suggestions = [];

            sortedNamespaces.forEach(ns => {
                originalGroups[ns].forEach(item => {
                    if (
                        item.shortName.toLowerCase().includes(query) ||
                        item.fullName.toLowerCase().includes(query)
                    ) {
                        suggestions.push({ item, namespace: ns });
                    }
                });
            });

            if (suggestions.length === 0) {
                treeContent.innerHTML = `
                        <div style="padding:12px; color:#6c757d; text-align:center;">
                            No structural matches found.
                        </div>`;
                return;
            }

            suggestions.slice(0, 30).forEach(item => {
                const div = document.createElement('div');
                div.className = 'class-item';
                div.innerHTML = `
                        <strong>${item.item.shortName}</strong>
                        <span style="font-size:0.7rem; color:#6c757d;">${item.namespace}</span>`;
                div.onclick = () => selectClassItem(item.item.fullName, item.item.shortName);
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
                    <div class="folder-children"></div>`;

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

    // GRAPH RENDER LOGIC
    function renderGraphMap() {
        clearCanvas();
        const container = document.getElementById('mynetwork');

        if (currentViewMode !== 'deadlocks' && !currentSelectedClass) {
            container.innerHTML = `
                    <div style="padding:40px; text-align:center; color:#6c757d;">
                        Please choose a Target Type from the directory stream above.
                    </div>`;
            return;
        }

        let nodes = [];
        let edges = [];

        // INHERITANCE VIEW
        if (currentViewMode === 'inheritance') {
            const connectedNodes = new Set([currentSelectedClass]);

            rawAssemblyData.inheritance.edges.forEach(e => {
                if (e.from === currentSelectedClass || e.to === currentSelectedClass) {
                    connectedNodes.add(e.from);
                    connectedNodes.add(e.to);
                }
            });

            nodes = rawAssemblyData.inheritance.nodes
                .filter(n => connectedNodes.has(n.id))
                .map(n => ({
                    id: n.id,
                    label: n.name,
                    shape: n.type === 'Interface' ? 'ellipse' : 'box',
                    color: n.type === 'Interface' ? '#e3f2fd' : '#fff3e0'
                }));

            edges = rawAssemblyData.inheritance.edges
                .filter(e => connectedNodes.has(e.from) && connectedNodes.has(e.to))
                .map(e => ({
                    from: e.from,
                    to: e.to,
                    arrows: 'to',
                    label: e.relation,
                    length: 220,
                    font: { align: 'horizontal', vadjust: -12, size: 11, color: '#495057' }
                }));
        }

        // METHOD CALL VIEW
        else if (currentViewMode === 'methods') {
            const relevantMethods = new Set();
            const methodToClassMap = {};

            rawAssemblyData.methodGraph.nodes.forEach(n => {
                methodToClassMap[n.id] = n.parentClass;
            });

            if (currentSelectedMethod) {
                const selectedMethodNode = rawAssemblyData.methodGraph.nodes.find(
                    n => n.id === currentSelectedMethod
                );

                if (!selectedMethodNode) {
                    container.innerHTML = `
                            <div style="padding:40px; text-align:center; color:#6c757d;">
                                Selected method was not found in the method graph.
                            </div>`;
                    return;
                }

                relevantMethods.add(currentSelectedMethod);
                methodToClassMap[currentSelectedMethod] = selectedMethodNode.parentClass;

                rawAssemblyData.methodGraph.edges.forEach(e => {
                    if (e.relation !== 'Calls') return;

                    if (e.to === currentSelectedMethod) {
                        relevantMethods.add(e.from);
                        relevantMethods.add(e.to);

                        if (e.fromClass) methodToClassMap[e.from] = e.fromClass;
                        if (e.toClass) methodToClassMap[e.to] = e.toClass;
                    }
                });
            } else {
                const classMethodIds = new Set(
                    rawAssemblyData.methodGraph.nodes
                        .filter(m => m.parentClass === currentSelectedClass)
                        .map(m => m.id)
                );

                rawAssemblyData.methodGraph.edges.forEach(e => {
                    if (e.relation !== 'Calls') return;

                    if (classMethodIds.has(e.from) || classMethodIds.has(e.to)) {
                        relevantMethods.add(e.from);
                        relevantMethods.add(e.to);

                        if (e.fromClass) methodToClassMap[e.from] = e.fromClass;
                        if (e.toClass) methodToClassMap[e.to] = e.toClass;
                    }
                });
            }

            relevantMethods.forEach(id => {
                const localNode = rawAssemblyData.methodGraph.nodes.find(n => n.id === id);

                let methodSignature = localNode ? localNode.name : '';
                let fullClassName = methodToClassMap[id] || (localNode ? localNode.parentClass : '');

                if (!methodSignature || !fullClassName) {
                    const cleanId = id;
                    const lastDot = cleanId.lastIndexOf('.');

                    if (lastDot > -1) {
                        fullClassName = cleanId.substring(0, lastDot);
                        methodSignature = cleanId.substring(lastDot + 1);
                    } else {
                        return;
                    }
                }

                const shortClassName = fullClassName.split('.').pop();

                nodes.push({
                    id: id,
                    label: `${shortClassName}.${methodSignature}`,
                    shape: 'box',
                    color: localNode ? '#e8f5e9' : '#f5f5f5',
                    title: `Full Path: ${fullClassName}.${methodSignature}`,
                    font: { multi: true }
                });
            });

            edges = rawAssemblyData.methodGraph.edges
                .filter(e => e.relation === 'Calls' && relevantMethods.has(e.from) && relevantMethods.has(e.to))
                .map(e => ({
                    from: e.from,
                    to: e.to,
                    arrows: 'to',
                    label: e.relation || 'Calls',
                    font: {
                        align: 'horizontal',
                        vadjust: -10,
                        size: 11,
                        color: '#495057'
                    }
                }));
        }
        // CIRCULAR DEPENDENCY VIEW
        else if (currentViewMode === 'deadlocks') {
            if (currentSelectedDeadlockIndex === null) {
                container.innerHTML = `
                        <div style="padding:40px; text-align:center; color:#6c757d;">
                            Please select a circular dependency from the sidebar.
                        </div>`;
                return;
            }

            const activeCyclePath = rawAssemblyData.deadlocks[currentSelectedDeadlockIndex];

            if (!activeCyclePath || activeCyclePath.length < 2) {
                container.innerHTML = `
                        <div style="padding:40px; text-align:center; color:#6c757d;">
                            The selected circular dependency is invalid.
                        </div>`;
                return;
            }

            const cycleStartNode = activeCyclePath[0];
            const cycleNodes = activeCyclePath.slice(0, -1);
            const cycleNodeSet = new Set(cycleNodes);

            // Nodes
            cycleNodes.forEach(id => {
                const localNode = rawAssemblyData.methodGraph.nodes.find(n => n.id === id);

                let methodSignature = localNode ? localNode.name : '';
                let fullClassName = localNode ? localNode.parentClass : '';

                if (!methodSignature) {
                    const cleanId = id;
                    const lastDot = cleanId.lastIndexOf('.');

                    if (lastDot > -1) {
                        fullClassName = cleanId.substring(0, lastDot);
                        methodSignature = cleanId.substring(lastDot + 1);
                    } else {
                        methodSignature = cleanId;
                        fullClassName = 'Unknown Class';
                    }
                }

                const shortClassName = fullClassName ? fullClassName.split('.').pop() : 'Unknown Class';
                const isCycleStart = id === cycleStartNode;

                nodes.push({
                    id: id,
                    label: isCycleStart ? `*${shortClassName}.${methodSignature}` : `${shortClassName}.${methodSignature}`,
                    shape: 'box',
                    borderWidth: isCycleStart ? 5 : 2,
                    borderWidthSelected: isCycleStart ? 6 : 3,
                    color: isCycleStart ? {
                        background: '#ffe0e0',
                        border: '#8b0000',
                        highlight: { background: '#ffcdd2', border: '#6b0000' },
                        hover: { background: '#ffcdd2', border: '#6b0000' }
                    } : {
                        background: '#fff5f5',
                        border: '#dc3545',
                        highlight: { background: '#ffe0e0', border: '#b71c1c' },
                        hover: { background: '#ffe0e0', border: '#b71c1c' }
                    },
                    font: {
                        multi: true,
                        color: isCycleStart ? '#8b0000' : '#721c24',
                        size: isCycleStart ? 15 : 13
                    },
                    margin: isCycleStart ? 18 : 12,
                    title: isCycleStart ? `
                            <strong>🔴 CYCLE START</strong><br>
                            ${fullClassName}.${methodSignature}<br><br>
                            This method is the starting point of the detected circular dependency.`
                        : `${fullClassName}.${methodSignature}`
                });
            });

            // Edges
            for (let i = 0; i < activeCyclePath.length - 1; i++) {
                const from = activeCyclePath[i];
                const to = activeCyclePath[i + 1];
                const isClosingEdge = i === activeCyclePath.length - 2;

                if (!cycleNodeSet.has(from) || !cycleNodeSet.has(to)) {
                    continue;
                }

                edges.push({
                    from: from,
                    to: to,
                    arrows: 'to',
                    color: {
                        color: '#dc3545',
                        highlight: '#8b0000',
                        hover: '#8b0000'
                    },
                    width: 2.5,
                    label: isClosingEdge ? 'return to' : 'call',
                    font: {
                        color: '#dc3545',
                        size: 11,
                        bold: isClosingEdge,
                        align: 'horizontal',
                        vadjust: -10
                    },
                    smooth: {
                        enabled: true,
                        type: 'dynamic',
                        roundness: 0.15
                    }
                });
            }
        }

        // Vis.js Network Options
        const options = {
            nodes: {
                font: { face: 'sans-serif', multi: true },
                margin: 12
            },
            edges: {
                selectionWidth: 2,
                arrows: { to: { enabled: true, scaleFactor: 0.8 } },
                font: { size: 11 }
            },
            physics: {
                solver: 'forceAtlas2Based',
                forceAtlas2Based: {
                    gravitationalConstant: -80,
                    centralGravity: 0.01,
                    springLength: currentViewMode === 'methods' ? 180 : currentViewMode === 'deadlocks' ? 220 : 120,
                    springConstant: 0.08,
                    damping: 0.4,
                    avoidOverlap: 1.0
                },
                stabilization: {
                    enabled: true,
                    iterations: 200,
                    updateInterval: 25
                }
            },
            interaction: {
                hover: true,
                navigationButtons: true,
                keyboard: { enabled: true }
            }
        };

        networkInstance = new vis.Network(container, { nodes, edges }, options);

        networkInstance.on('stabilizationIterationsDone', function () {
            networkInstance.setOptions({ physics: false });

            if (currentViewMode === 'deadlocks' && currentSelectedDeadlockIndex !== null) {
                const selectedCycle = rawAssemblyData.deadlocks[currentSelectedDeadlockIndex];
                if (selectedCycle && selectedCycle.length > 0) {
                    const cycleStart = selectedCycle[0];
                    if (networkInstance.body.nodes[cycleStart]) {
                        networkInstance.selectNodes([cycleStart]);
                    }
                }
            }
        });
    }

    // EVENT LISTENERS
    document.getElementById('searchClass').addEventListener('input', e => renderTreeView(e.target.value));
    document.getElementById('searchMethod').addEventListener('input', e => populateMethodsDropdown(e.target.value));

    document.getElementById('assemblyTrigger').onclick = () => {
        if (loadedAssemblies.size > 0) {
            document.getElementById('assemblyMenu').style.display = 'flex';
        }
    };

    document.getElementById('dropdownTrigger').onclick = () => {
        document.getElementById('dropdownMenu').style.display = 'flex';
    };

    document.getElementById('methodTrigger').onclick = () => {
        document.getElementById('methodMenu').style.display = 'flex';
    };

    // DISMISS DROPDOWNS ON OUTSIDE CLICK
    window.onclick = function (event) {
        const classWrapper = document.getElementById('classDropdownWrapper');
        const methodWrapper = document.getElementById('methodDropdownWrapper');
        const assemblyWrapper = document.getElementById('assemblyDropdownWrapper');

        if (classWrapper && !classWrapper.contains(event.target)) {
            document.getElementById('dropdownMenu').style.display = 'none';
        }

        if (methodWrapper && !methodWrapper.contains(event.target)) {
            document.getElementById('methodMenu').style.display = 'none';
        }

        if (assemblyWrapper && !assemblyWrapper.contains(event.target)) {
            document.getElementById('assemblyMenu').style.display = 'none';
        }
    };
