let rawAssemblyData = { classes: [], inheritance: { nodes: [], edges: [] }, methodGraph: { nodes: [], edges: [] } };
let activeStructuralMapCache = {};
let assemblyDeadlocksCache = {};

let currentSelectedAssembly = null;
let currentViewMode = 'inheritance';
let currentSelectedClass = null;
let currentSelectedMethod = null;
let currentSelectedDeadlockIndex = null;
let networkInstance = null;
let messageTimeout = null;

let isDraggingSplitter = false;
let cachedSidebarWidth = 350;

function parseRawDataToAssembliesMap(data) {
    const map = {};
    if (!data || !data.classes) return map;

    data.classes.forEach(cls => {
        // 1. Check if the item itself is a plain string from the backend
        let fullClassName = "";
        if (typeof cls === 'string') {
            fullClassName = cls;
        } else if (cls && typeof cls === 'object') {
            fullClassName = cls.id || cls.fullName || "";
        }

        if (!fullClassName) return; // Skip if empty

        // 2. Derive Assembly and Namespace dynamically from the dot-notation path
        // (Since backend sends a plain string, we fallback safely)
        const asm = (cls && cls.assembly) || "Default Assembly";

        // Extract namespace (everything before the last dot)
        let ns = "(No Namespace)";
        const lastDotIndex = fullClassName.lastIndexOf('.');
        if (lastDotIndex !== -1) {
            ns = fullClassName.substring(0, lastDotIndex);
        } else if (cls && cls.namespace) {
            ns = cls.namespace;
        }

        // Extract short class name (everything after the last dot)
        const shortName = lastDotIndex !== -1 ? fullClassName.substring(lastDotIndex + 1) : fullClassName;

        // 3. Initialize structural nodes in map if missing
        if (!map[asm]) map[asm] = {};
        if (!map[asm][ns]) map[asm][ns] = [];

        map[asm][ns].push({
            fullName: fullClassName,
            shortName: shortName
        });
    });
    return map;
}

function getCleanMethodSignature(rawMethodId) {
    if (!rawMethodId) return "";

    let openParenIdx = rawMethodId.indexOf('(');
    if (openParenIdx === -1) {
        return rawMethodId.split('.').pop() + "()";
    }

    let methodPath = rawMethodId.substring(0, openParenIdx);
    let paramsContent = rawMethodId.substring(openParenIdx + 1, rawMethodId.lastIndexOf(')'));
    let methodName = methodPath.split('.').pop();

    if (!paramsContent.trim()) {
        return methodName + "()";
    }

    let formattedParams = paramsContent.split(',').map((param) => param.trim()).join(', ');
    return `${methodName}(${formattedParams})`;
}

function showLoader(statusText) {
    const loader = document.getElementById('canvasLoaderOverlay');
    const txt = document.getElementById('loaderStatusText');
    if (loader && txt) { txt.textContent = statusText; loader.style.display = 'flex'; }
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
    const networkContainer = document.getElementById('mynetwork');
    if (networkInstance !== null) { networkInstance.destroy(); networkInstance = null; }
    if (networkContainer) networkContainer.style.display = 'none';
}

function handleTreeNodeToggle(event, element, optionalAsmName = null) {
    event.stopPropagation();
    const treeNodeContainer = element.closest('.tree-node');
    if (treeNodeContainer) {
        treeNodeContainer.classList.toggle('collapsed');
    }
    if (optionalAsmName) {
        selectAssemblyContext(optionalAsmName);
    }
}

function selectAssemblyContext(asmName) {
    currentSelectedAssembly = asmName;
    currentSelectedDeadlockIndex = null;
    currentSelectedClass = null;
    currentSelectedMethod = null;

    const methodTriggerText = document.getElementById('methodTriggerText');
    if (methodTriggerText) methodTriggerText.textContent = "-- Select Method --";

    const searchMethod = document.getElementById('searchMethod');
    if (searchMethod) searchMethod.value = "";

    const methodTreeContent = document.getElementById('methodTreeContent');
    if (methodTreeContent) {
        methodTreeContent.innerHTML = `<div style="padding:12px; color:#6c757d; text-align:center;">Select a class to scan execution tokens.</div>`;
    }

    const methodTrigger = document.getElementById('methodTrigger');
    if (methodTrigger) {
        methodTrigger.classList.add('disabled');
    }

    document.querySelectorAll('.ilspy-tree .tree-node').forEach(node => node.classList.remove('active'));

    document.querySelectorAll('.tree-node-header').forEach(header => {
        const titleSpan = header.querySelector('.asm-title-text');
        if (titleSpan && titleSpan.textContent === asmName) {
            header.classList.add('selected-asm');
        } else {
            header.classList.remove('selected-asm');
        }
    });

    renderDeadlocksSidePanel();
    updateDeadlockGlobalBadgeCount();
    updateSelectionBanner();
    updatePlaceholderPrompt();
    clearCanvas();
}

function updateSelectionBanner() {
    const banner = document.getElementById('bannerText');
    if (!banner) return;

    if (currentViewMode === 'deadlocks') {
        if (currentSelectedDeadlockIndex !== null) {
            banner.innerHTML = `<span style="color: #dc3545; font-weight: bold;">Circular Loop Isolation Stack [Trace #${currentSelectedDeadlockIndex + 1}]</span>`;
        } else if (currentSelectedAssembly) {
            banner.innerHTML = `<span style="color: #007bff; font-weight: 600;">Scoping loops in: ${currentSelectedAssembly}</span>`;
        } else {
            banner.innerHTML = `<span style="color: #6c757d; font-style: italic;">No active cyclic dependency trace isolated globally</span>`;
        }
        return;
    }

    if (!currentSelectedClass) {
        if (currentSelectedAssembly) {
            banner.innerHTML = `<span style="color: #007bff; font-weight: 600;">Scoping component relations in: ${currentSelectedAssembly}</span>`;
        } else {
            banner.innerHTML = `<span style="color: #6c757d; font-style: italic;">No entity selected</span>`;
        }
    } else {
        let text = `<span style="color: #1e40af; font-weight: 600;">${currentSelectedClass}</span>`;
        if (currentSelectedMethod) {
            const cleanName = getCleanMethodSignature(currentSelectedMethod);
            text += `<span style="color: #6c757d;">.</span><span style="color: #155724; font-weight: 600; border-radius:3px;">${cleanName}</span>`;
        }
        banner.innerHTML = text;
    }
}

function selectClass(classId) {
    currentSelectedClass = classId;
    currentSelectedMethod = null;

    const methodTriggerText = document.getElementById('methodTriggerText');
    if (methodTriggerText) methodTriggerText.textContent = "-- Select Target Method --";

    const methodTrigger = document.getElementById('methodTrigger');
    if (currentViewMode === 'methods') {
        if (methodTrigger) methodTrigger.classList.remove('disabled');
        populateMethodsDropdown();
    }

    updateSelectionBanner();
    updatePlaceholderPrompt();
    renderGraphMap();
}

function selectClassItem(fullName, elementId, assemblyName) {
    currentSelectedClass = fullName;
    currentSelectedAssembly = assemblyName;
    currentSelectedMethod = null;
    currentSelectedDeadlockIndex = null;

    const triggerText = document.getElementById('methodTriggerText');
    if (triggerText) triggerText.textContent = "-- Select Target Method --";

    try {
        const invisibleInput = document.createElement("input");
        invisibleInput.type = "text";
        invisibleInput.value = fullName;
        invisibleInput.style.position = "fixed";
        invisibleInput.style.opacity = "0";
        invisibleInput.style.pointerEvents = "none";
        invisibleInput.style.zIndex = "-1";

        document.body.appendChild(invisibleInput);
        invisibleInput.focus({ preventScroll: true });
        invisibleInput.select();
        invisibleInput.setSelectionRange(0, 99999);

        const success = document.execCommand("copy");
        document.body.removeChild(invisibleInput);

        if (success) {
            showMessage(`📋 Copied to clipboard: ${fullName.split('.').pop()}`, 'success');
        } else if (navigator.clipboard && navigator.clipboard.writeText) {
            navigator.clipboard.writeText(fullName).catch(() => { });
        }
    } catch (err) {
        console.error("Clipboard operational exception skipped:", err);
    }

    document.querySelectorAll('.tree-node').forEach(node => node.classList.remove('active'));
    const targetNode = document.getElementById(elementId);
    if (targetNode) targetNode.classList.add('active');

    document.querySelectorAll('.tree-node-header').forEach(header => {
        const titleSpan = header.querySelector('.asm-title-text');
        if (titleSpan && titleSpan.textContent === assemblyName) {
            header.classList.add('selected-asm');
        } else {
            header.classList.remove('selected-asm');
        }
    });

    if (currentViewMode === 'methods') {
        populateMethodsDropdown();
    } else if (currentViewMode === 'deadlocks') {
        renderDeadlocksSidePanel();
    }

    updateDeadlockGlobalBadgeCount();
    updateSelectionBanner();
    renderGraphMap();
}

function updatePlaceholderPrompt() {
    const msgLayer = document.getElementById('canvasPlaceholderMessage');
    if (!msgLayer) return;

    if (currentViewMode === 'deadlocks') {
        if (currentSelectedDeadlockIndex === null) {
            const contextPrefix = currentSelectedAssembly ? `for <strong>${currentSelectedAssembly}</strong>` : "";
            msgLayer.innerHTML = `<strong>Circular Dependency View Loop Trace</strong><br/><br/>Please select a detected loop execution trace from the Right panel directory ${contextPrefix} to construct mapping flows.`;
            msgLayer.style.display = 'flex';
        } else {
            msgLayer.style.display = 'none';
        }
        return;
    }

    if (!currentSelectedClass) {
        msgLayer.innerHTML = `Please select any class to see its methods.`;
        msgLayer.style.display = 'flex';
    } else if (currentViewMode === 'methods' && !currentSelectedMethod) {
        msgLayer.innerHTML = `<strong>Class Hooked:</strong> <span style="color:#007bff;">${currentSelectedClass.split('.').pop()}</span><br/><br/>Use the <strong>Target Execution Member</strong> dropdown menu panel above to scan operational callee loops.`;
        msgLayer.style.display = 'flex';
    } else {
        msgLayer.style.display = 'none';
    }
}

function switchViewMode(mode) {
    currentViewMode = mode;

    const tabInheritance = document.getElementById('tabInheritance');
    const tabMethods = document.getElementById('tabMethods');
    const tabDeadlocks = document.getElementById('tabDeadlocks');

    if (tabInheritance) tabInheritance.classList.toggle('active', mode === 'inheritance');
    if (tabMethods) tabMethods.classList.toggle('active', mode === 'methods');
    if (tabDeadlocks) tabDeadlocks.classList.toggle('active', mode === 'deadlocks');

    const methodToolbar = document.getElementById('methodTargetToolbar');
    const legendMethodItem = document.getElementById('legendMethodItem');
    const legendDeadlockItem = document.getElementById('legendDeadlockItem');
    const deadlocksPanel = document.getElementById('deadlocksSidePanel');

    if (mode === 'methods') {
        if (methodToolbar) methodToolbar.style.display = 'flex';
        if (legendMethodItem) legendMethodItem.style.display = 'flex';
        if (legendDeadlockItem) legendDeadlockItem.style.display = 'none';
        if (deadlocksPanel) deadlocksPanel.style.display = 'none';
        if (currentSelectedClass) populateMethodsDropdown();
    } else if (mode === 'deadlocks') {
        if (methodToolbar) methodToolbar.style.display = 'none';
        if (legendMethodItem) legendMethodItem.style.display = 'none';
        if (legendDeadlockItem) legendDeadlockItem.style.display = 'flex';
        if (deadlocksPanel) deadlocksPanel.style.display = 'flex';
        renderDeadlocksSidePanel();
    } else {
        if (methodToolbar) methodToolbar.style.display = 'none';
        if (legendMethodItem) legendMethodItem.style.display = 'none';
        if (legendDeadlockItem) legendDeadlockItem.style.display = 'none';
        if (deadlocksPanel) deadlocksPanel.style.display = 'none';
    }
    updateSelectionBanner();
    renderGraphMap();
}

function selectMethodItem(methodId, methodName) {
    currentSelectedMethod = methodId;
    const triggerText = document.getElementById('methodTriggerText');
    const methodMenu = document.getElementById('methodMenu');

    if (triggerText) triggerText.textContent = methodName;
    if (methodMenu) methodMenu.style.display = 'none';

    updateSelectionBanner();
    renderGraphMap();
}

function populateMethodsDropdown(filterText = "") {
    const container = document.getElementById('methodTreeContent');
    const trigger = document.getElementById('methodTrigger');
    if (!container || !currentSelectedClass) return;

    container.innerHTML = "";
    const query = filterText.toLowerCase().trim();

    if (!rawAssemblyData || !rawAssemblyData.methodGraph || !rawAssemblyData.methodGraph.nodes) return;
    const methods = rawAssemblyData.methodGraph.nodes.filter(m => m.parentClass === currentSelectedClass);

    if (methods.length === 0) {
        container.innerHTML = `<div style="padding:12px; color:#6c757d; text-align:center;">No scanning execution tokens found in type definitions.</div>`;
        if (trigger) trigger.classList.add('disabled');
        return;
    }

    if (trigger) trigger.classList.remove('disabled');

    const processedList = methods.map(m => {
        return { id: m.id, cleanDisplay: getCleanMethodSignature(m.id || m.name) };
    });

    const filtered = processedList.filter(m => m.cleanDisplay.toLowerCase().includes(query));
    filtered.sort((a, b) => a.cleanDisplay.localeCompare(b.cleanDisplay)).forEach(method => {
        const div = document.createElement('div');
        div.className = 'method-item';
        div.innerHTML = `<strong>${method.cleanDisplay}</strong>`;
        div.onclick = () => selectMethodItem(method.id, method.cleanDisplay);
        container.appendChild(div);
    });
}

function renderDeadlocksSidePanel() {
    const deadlockContainer = document.getElementById('deadlockList');
    const subtitle = document.getElementById('deadlocksPanelSubtitle');
    if (!deadlockContainer) return;
    deadlockContainer.innerHTML = '';

    let loopsToRender = [];

    if (currentSelectedAssembly && assemblyDeadlocksCache[currentSelectedAssembly]) {
        loopsToRender = assemblyDeadlocksCache[currentSelectedAssembly];
        if (subtitle) subtitle.innerHTML = `Loops discovered inside <b style="color:#007bff;">${currentSelectedAssembly}</b>:`;
    } else {
        if (subtitle) subtitle.innerHTML = `Select an assembly from the Left Explorer to scope loop pipelines.`;
        deadlockContainer.innerHTML = '<div style="color: #6c757d; font-size:0.85rem; font-style:italic; padding: 10px; text-align:center;">No active scope. Click an assembly header or a type in the explorer tree.</div>';
        return;
    }

    if (loopsToRender.length === 0) {
        deadlockContainer.innerHTML = '<div style="color: #28a745; font-size:0.85rem; font-weight:bold; padding: 10px; background: #e2f0d9; border-radius:4px;">✓ No structural loops detected in this assembly.</div>';
        return;
    }

    loopsToRender.forEach((cycle, index) => {
        const btn = document.createElement('button');
        btn.style = "text-align: left; padding: 10px; background: #fff5f5; border: 1px solid #feb2b2; border-radius: 6px; cursor: pointer; font-size: 0.8rem; font-family: monospace; width: 100%; box-sizing: border-box; margin-bottom: 4px;";
        const originNode = getCleanMethodSignature(cycle[0]);
        const targetNode = getCleanMethodSignature(cycle[cycle.length - 2]);

        if (currentSelectedDeadlockIndex === index) {
            btn.style.background = '#fed7d7';
        }

        btn.innerHTML = `<b style="color: #c53030;">[Loop #${index + 1}]</b> <br/> ${originNode} ➔ ${targetNode}`;
        btn.onclick = () => {
            Array.from(deadlockContainer.children).forEach(child => child.style.background = '#fff5f5');
            btn.style.background = '#fed7d7';
            currentSelectedDeadlockIndex = index;
            updateSelectionBanner();
            renderGraphMap();
        };
        deadlockContainer.appendChild(btn);
    });
}

function updateDeadlockGlobalBadgeCount() {
    const badge = document.getElementById('deadlockCount');
    if (!badge) return;

    if (currentSelectedAssembly && assemblyDeadlocksCache[currentSelectedAssembly]) {
        badge.innerText = assemblyDeadlocksCache[currentSelectedAssembly].length;
    } else {
        let sum = 0;
        Object.values(assemblyDeadlocksCache).forEach(arr => sum += arr.length);
        badge.innerText = sum;
    }
}

function buildILSpyTree(assembliesMap, searchFilter = "") {
    const rootContainer = document.getElementById('ilspyTreeRoot');
    if (!rootContainer) return;
    rootContainer.innerHTML = "";

    let nodeCounter = 0;
    const query = searchFilter.toLowerCase().trim();
    let assembliesWithMatches = 0;

    let activeAsmName = "";
    let activeNsName = "";

    if (currentSelectedClass && query === "") {
        for (const [asm, namespaces] of Object.entries(assembliesMap)) {
            for (const [ns, classes] of Object.entries(namespaces)) {
                if (classes.some(c => c.fullName === currentSelectedClass)) {
                    activeAsmName = asm;
                    activeNsName = ns;
                    break;
                }
            }
            if (activeAsmName) break;
        }
    }

    Object.keys(assembliesMap).sort().forEach(asmName => {
        let asmHasMatch = false;
        let processedNamespaces = {};
        const namespacesMap = assembliesMap[asmName];

        Object.keys(namespacesMap).forEach(nsName => {
            const classesList = namespacesMap[nsName];
            const filteredClasses = classesList.filter(cls => {
                const sName = cls.shortName || cls.name || "";
                return query === "" ||
                    sName.toLowerCase().includes(query) ||
                    nsName.toLowerCase().includes(query) ||
                    asmName.toLowerCase().includes(query);
            });

            if (filteredClasses.length > 0) {
                processedNamespaces[nsName] = filteredClasses;
                asmHasMatch = true;
            }
        });

        if (!asmHasMatch) return;
        assembliesWithMatches++;

        nodeCounter++;
        const asmNodeId = `node_asm_${nodeCounter}`;
        const asmNode = document.createElement('div');

        const expandAsm = (query !== "");
        asmNode.className = expandAsm ? 'tree-node' : 'tree-node collapsed';
        asmNode.id = asmNodeId;

        const isSelectedAsm = (asmName === currentSelectedAssembly);
        const headerClassStr = isSelectedAsm ? 'tree-node-header selected-asm' : 'tree-node-header';

        asmNode.innerHTML = `
            <div class="${headerClassStr}" id="${asmNodeId}_header">
                <span class="tree-node-icon" id="${asmNodeId}_arrow">▼</span>
                <span style="font-weight:600; color:#1e293b;" class="asm-title-text">${asmName}</span>
            </div>
            <div class="tree-node-children"></div>
        `;

        // Bind collapse arrow explicitly
        const arrowSpan = asmNode.querySelector(`#${asmNodeId}_arrow`);
        if (arrowSpan) {
            arrowSpan.addEventListener('click', (e) => {
                handleTreeNodeToggle(e, document.getElementById(`${asmNodeId}_header`), asmName.replace(/'/g, "\\'"));
            });
        }

        asmNode.querySelector('.asm-title-text').onclick = (e) => {
            e.stopPropagation();
            selectAssemblyContext(asmName);
        };

        const asmChildrenContainer = asmNode.querySelector('.tree-node-children');

        Object.keys(processedNamespaces).sort().forEach(nsName => {
            nodeCounter++;
            const nsNodeId = `node_ns_${nodeCounter}`;
            const nsNode = document.createElement('div');

            const expandNs = (query !== "") || (asmName === activeAsmName && nsName === activeNsName);
            nsNode.className = expandNs ? 'tree-node' : 'tree-node collapsed';
            nsNode.id = nsNodeId;

            nsNode.innerHTML = `
                <div class="tree-node-header">
                    <span class="tree-node-icon">▼</span>
                    <span style="color:#334155;">${nsName || '(No Namespace)'}</span>
                </div>
                <div class="tree-node-children"></div>
            `;

            // Make the whole namespace element header clickable for accordion support
            nsNode.querySelector('.tree-node-header').onclick = (e) => {
                handleTreeNodeToggle(e, nsNode.querySelector('.tree-node-header'));
            };

            const nsChildrenContainer = nsNode.querySelector('.tree-node-children');
            const classesList = processedNamespaces[nsName];

            // Added fallback protection (a.shortName || a.name || "") to prevent localeCompare undefined errors
            classesList.sort((a, b) => {
                const nameA = a.shortName || a.name || "";
                const nameB = b.shortName || b.name || "";
                return nameA.localeCompare(nameB);
            }).forEach(cls => {
                nodeCounter++;
                const clsNodeId = `node_cls_${nodeCounter}`;
                const clsNode = document.createElement('div');
                clsNode.className = 'tree-node';
                clsNode.id = clsNodeId;

                if (currentSelectedClass === cls.fullName) {
                    clsNode.classList.add('active');
                }

                clsNode.innerHTML = `
                    <div class="tree-node-header" style="padding-left:14px;">
                        <span style="font-size:0.85rem;">${cls.shortName || cls.name || ""}</span>
                    </div>
                `;

                clsNode.querySelector('.tree-node-header').onclick = (e) => {
                    e.stopPropagation();
                    selectClassItem(cls.fullName, clsNodeId, asmName);
                };

                nsChildrenContainer.appendChild(clsNode);
            });

            asmChildrenContainer.appendChild(nsNode);
        });

        rootContainer.appendChild(asmNode);
    });

    if (assembliesWithMatches === 0) {
        rootContainer.innerHTML = `<div style="padding: 15px; color: #737378; font-style: italic; text-align: center; font-size:0.85rem;">No component matches found.</div>`;
    }
}
function buildExplorerNavigationTree() {
    const sidebarTree = document.getElementById('sidebarTree');
    if (!sidebarTree) return;
    sidebarTree.innerHTML = "";

    if (!rawAssemblyData || !rawAssemblyData.classes || rawAssemblyData.classes.length === 0) {
        sidebarTree.innerHTML = '<div style="padding:10px; color:#6c757d; font-size:0.85rem;">No assemblies parsed.</div>';
        return;
    }

    const namespaceGroupings = {};
    rawAssemblyData.classes.forEach(cls => {
        let fullClassName = typeof cls === 'string' ? cls : (cls.id || "");
        let namespace = "(No Namespace)";

        const lastDotIndex = fullClassName.lastIndexOf('.');
        if (lastDotIndex !== -1) {
            namespace = fullClassName.substring(0, lastDotIndex);
        } else if (cls && cls.namespace) {
            namespace = cls.namespace;
        }

        if (!namespaceGroupings[namespace]) {
            namespaceGroupings[namespace] = [];
        }
        namespaceGroupings[namespace].push(cls);
    });

    Object.keys(namespaceGroupings).sort().forEach(namespaceName => {
        const namespaceId = "ns-" + namespaceName.replace(/[^a-zA-Z0-9]/g, "-");
        const namespaceItem = document.createElement('div');
        namespaceItem.className = 'tree-node namespace-node';
        namespaceItem.innerHTML = `
            <span class="tree-toggle">▶</span>
            <span class="node-icon">📦</span>
            <span class="node-text" title="${namespaceName}">${namespaceName}</span>
        `;

        const childrenContainer = document.createElement('div');
        childrenContainer.id = namespaceId;
        childrenContainer.className = 'tree-children';
        childrenContainer.style.display = 'none';

        namespaceGroupings[namespaceName].sort((a, b) => {
            const nameA = typeof a === 'string' ? a.split('.').pop() : (a.name || "");
            const nameB = typeof b === 'string' ? b.split('.').pop() : (b.name || "");
            return nameA.localeCompare(nameB);
        }).forEach(cls => {
            const fullId = typeof cls === 'string' ? cls : (cls.id || "");
            const displayName = fullId.split('.').pop() || "Unknown Class";

            const classItem = document.createElement('div');
            classItem.className = `tree-node class-node ${currentSelectedClass === fullId ? 'selected' : ''}`;
            classItem.innerHTML = `
                <span class="node-icon">🔷</span>
                <span class="node-text" title="${displayName}">${displayName}</span>
            `;

            classItem.addEventListener('click', (e) => {
                e.stopPropagation();
                document.querySelectorAll('.class-node').forEach(node => node.classList.remove('selected'));
                classItem.classList.add('selected');
                selectClass(fullId);
            });

            childrenContainer.appendChild(classItem);
        });

        namespaceItem.addEventListener('click', () => {
            const isHidden = childrenContainer.style.display === 'none';
            childrenContainer.style.display = isHidden ? 'block' : 'none';
            namespaceItem.querySelector('.tree-toggle').textContent = isHidden ? '▼' : '▶';
        });

        sidebarTree.appendChild(namespaceItem);
        sidebarTree.appendChild(childrenContainer);
    });
}
function renderGraphMap() {
    clearCanvas();
    updatePlaceholderPrompt();
    const container = document.getElementById('mynetwork');
    if (!container) return;

    if (currentViewMode !== 'deadlocks' && !currentSelectedClass) return;
    if (currentViewMode === 'methods' && !currentSelectedMethod) return;
    if (currentViewMode === 'deadlocks' && currentSelectedDeadlockIndex === null) return;

    container.style.display = 'block';
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
            from: e.from, to: e.to, arrows: 'to', label: e.relation, length: 220,
            font: { align: 'horizontal', vadjust: -12, size: 11, color: '#495057' }
        }));

    } else if (currentViewMode === 'methods') {
        const relevantMethods = new Set();
        const methodToClassMap = {};

        rawAssemblyData.methodGraph.nodes.forEach(n => {
            methodToClassMap[n.id] = n.parentClass;
        });

        relevantMethods.add(currentSelectedMethod);
        rawAssemblyData.methodGraph.edges.forEach(e => {
            if (e.from === currentSelectedMethod || e.to === currentSelectedMethod) {
                relevantMethods.add(e.from); relevantMethods.add(e.to);
                if (e.fromClass) methodToClassMap[e.from] = e.fromClass;
                if (e.toClass) methodToClassMap[e.to] = e.toClass;
            }
        });

        relevantMethods.forEach(id => {
            const localNode = rawAssemblyData.methodGraph.nodes.find(n => n.id === id);
            let fullClassName = methodToClassMap[id] || (localNode ? localNode.parentClass : "");
            let methodName = getCleanMethodSignature(localNode ? localNode.id : id);

            if (!fullClassName) {
                const cleanId = id.split('(')[0];
                const lastDot = cleanId.lastIndexOf('.');
                fullClassName = lastDot > -1 ? id.substring(0, lastDot) : "Unknown Class";
            }

            const shortClassName = fullClassName.split('.').pop();
            nodes.push({
                id: id, label: `${shortClassName}.${methodName}`, shape: 'box',
                color: localNode ? '#e8f5e9' : '#f5f5f5', title: `Full Path: ${fullClassName}.${methodName}`,
                font: { multi: true, bold: { color: '#212529' } }
            });
        });

        edges = rawAssemblyData.methodGraph.edges.filter(e => relevantMethods.has(e.from) && relevantMethods.has(e.to)).map(e => ({
            from: e.from, to: e.to, arrows: 'to'
        }));

    } else if (currentViewMode === 'deadlocks') {
        const targetedAssemblyLoops = assemblyDeadlocksCache[currentSelectedAssembly];
        if (!targetedAssemblyLoops) return;

        const activeCyclePath = targetedAssemblyLoops[currentSelectedDeadlockIndex];
        if (!activeCyclePath) return;

        const nodesInCycle = new Set(activeCyclePath);
        nodesInCycle.forEach(id => {
            const localNode = rawAssemblyData.methodGraph.nodes.find(n => n.id === id);
            let fullClassName = localNode ? localNode.parentClass : "";
            let methodName = getCleanMethodSignature(id);

            if (!fullClassName) {
                const cleanId = id.split('(')[0];
                const lastDot = cleanId.lastIndexOf('.');
                fullClassName = lastDot > -1 ? cleanId.substring(0, lastDot) : "External Reference";
            }

            const shortClassName = fullClassName.split('.').pop();
            nodes.push({
                id: id, label: `${shortClassName}.${methodName}`, shape: 'box', borderWidth: 2,
                color: { background: '#ffebeb', border: '#dc3545', highlight: { background: '#ffcdd2', border: '#b71c1c' } },
                font: { multi: true, color: '#721c24' }, title: `Deadlock Component: ${fullClassName}.${methodName}`
            });
        });

        for (let i = 0; i < activeCyclePath.length - 1; i++) {
            edges.push({
                from: activeCyclePath[i], to: activeCyclePath[i + 1], arrows: 'to',
                color: { color: '#dc3545', highlight: '#b71c1c' }, width: 3, label: 'causes lock',
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
                gravitationalConstant: -80, centralGravity: 0.01,
                springLength: currentViewMode === 'methods' ? 180 : 120,
                springConstant: 0.08, damping: 0.4, avoidOverlap: 1.0
            }
        }
    };

    networkInstance = new vis.Network(container, { nodes, edges }, options);
    networkInstance.on("stabilizationIterationsDone", function () {
        networkInstance.setOptions({ physics: false });
    });
}

// Global Event Registration Pipeline
document.addEventListener("DOMContentLoaded", () => {
    // 1. Bind Layout View Tab Controls
    const tabInheritance = document.getElementById('tabInheritance');
    const tabMethods = document.getElementById('tabMethods');
    const tabDeadlocks = document.getElementById('tabDeadlocks');

    if (tabInheritance) tabInheritance.addEventListener('click', () => switchViewMode('inheritance'));
    if (tabMethods) tabMethods.addEventListener('click', () => switchViewMode('methods'));
    if (tabDeadlocks) tabDeadlocks.addEventListener('click', () => switchViewMode('deadlocks'));

    // 2. Bind Live Filter Controls for Assemblies Explorer and Dropdowns
    const searchSidebarTree = document.getElementById('searchSidebarTree');
    if (searchSidebarTree) {
        searchSidebarTree.addEventListener('input', function () {
            const asmMap = parseRawDataToAssembliesMap(rawAssemblyData);
            buildILSpyTree(asmMap, this.value);
        });
    }

    const searchMethod = document.getElementById('searchMethod');
    if (searchMethod) {
        searchMethod.addEventListener('input', function () {
            populateMethodsDropdown(this.value);
        });
    }

    // 3. Dropdown Menu Trigger Visibility Managers
    const methodTrigger = document.getElementById('methodTrigger');
    const methodMenu = document.getElementById('methodMenu');
    const closeMethodMenuBtn = document.getElementById('closeMethodMenuBtn');

    if (methodTrigger && methodMenu) {
        methodTrigger.addEventListener('click', function (e) {
            if (this.classList.contains('disabled')) return;
            e.stopPropagation();
            methodMenu.style.display = methodMenu.style.display === 'flex' ? 'none' : 'flex';
        });
    }

    if (closeMethodMenuBtn && methodMenu) {
        closeMethodMenuBtn.addEventListener('click', function (e) {
            e.stopPropagation();
            methodMenu.style.display = 'none';
        });
    }

    window.addEventListener('click', function (event) {
        const methodWrapper = document.getElementById('methodDropdownWrapper');
        if (methodWrapper && !methodWrapper.contains(event.target)) {
            if (methodMenu) methodMenu.style.display = "none";
        }
    });

    // 4. Sidebar Splitter Click and Drag Triggers
    const splitter = document.getElementById('sidebarSplitter');
    const sidebar = document.getElementById('ilspySidebar');

    if (splitter && sidebar) {
        splitter.addEventListener('mousedown', function (e) {
            e.preventDefault();
            isDraggingSplitter = true;
            splitter.classList.add('dragging');
        });

        document.addEventListener('mousemove', function (e) {
            if (!isDraggingSplitter) return;
            const workspace = document.getElementById('workspaceContainer');
            const containerLeft = workspace ? workspace.getBoundingClientRect().left : 0;
            let targetWidth = e.clientX - containerLeft;

            if (targetWidth < 20) targetWidth = 0;
            if (targetWidth > 650) targetWidth = 650;

            sidebar.style.width = targetWidth + 'px';
            if (targetWidth > 0) {
                cachedSidebarWidth = targetWidth;
            }
        });

        document.addEventListener('mouseup', function () {
            if (isDraggingSplitter) {
                isDraggingSplitter = false;
                splitter.classList.remove('dragging');
                if (networkInstance) networkInstance.fit();
            }
        });

        splitter.addEventListener('dblclick', function () {
            const currentWidth = sidebar.getBoundingClientRect().width;
            sidebar.style.width = currentWidth > 0 ? '0px' : cachedSidebarWidth + 'px';
            setTimeout(() => { if (networkInstance) networkInstance.fit(); }, 150);
        });
    }

    // 5. High-Performance Asynchronous File Upload Processing Engine
    const dllFileInput = document.getElementById('dllFile');
    const uploadLabel = document.getElementById('fileUploadLabelElement');

    if (dllFileInput) {
        dllFileInput.addEventListener('change', async (e) => {
            e.preventDefault();
            const files = e.target.files;
            if (!files || files.length === 0) return;

            const targetFile = files[0];
            if (uploadLabel) uploadLabel.textContent = targetFile.name;

            showLoader("Uploading assembly binaries to backend analysis pipeline...");

            const formData = new FormData();
            formData.append('file', targetFile); // Matches 'IFormFile file' in Controller

            try {
                const response = await fetch('/api/Analysis/analyze', {
                    method: 'POST',
                    body: formData
                });

                if (!response.ok) {
                    const errorText = await response.text();
                    throw new Error(errorText || `Server responded with standard HTTP error status: ${response.status}`);
                }

                const resultData = await response.json();
                hideLoader();
                showMessage("Assembly structure successfully compiled by backend analysis server!", "success");

                // Bind parsed response properties safely
                rawAssemblyData = resultData;
                assemblyDeadlocksCache = resultData.assemblyDeadlocks || {};

                // Re-hydrate components tree layouts
                const asmMap = parseRawDataToAssembliesMap(rawAssemblyData);
                buildILSpyTree(asmMap, "");
                buildExplorerNavigationTree();

                // Initialize clean default view context settings
                switchViewMode('inheritance');

            } catch (error) {
                hideLoader();
                console.error("Backend server transmission exception trace:", error);
                showMessage(`Transmission Error: ${error.message}`, "error");
            }
        });
    }
});