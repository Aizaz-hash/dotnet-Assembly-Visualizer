/*
 * The list of views and the groups they are shown in, in one place. The tab bar, the "which view is active" logic and the tests read it,
 * so a view is added, moved to another group or merged into another screen here instead of in several files.
 *
 * The groups follow the workflow: open a DLL -> Overview -> Analysis -> Security & risk -> API Compatibility -> Detailed views.
 * `section` is the id of the tab and the element that shows it (tab<Section>, view<Section>).
 */
(function (root, factory) {
    if (typeof module === 'object' && module.exports) module.exports = factory();
    else root.ViewRegistry = factory();
})(typeof self !== 'undefined' ? self : this, function () {
    'use strict';

    const GROUPS = [
        { id: 'groupSummary', label: 'Overview', hint: 'Start here: what this assembly is' },
        { id: 'groupAnalysis', label: 'Analysis', hint: 'The size of the assembly and how its namespaces depend on each other' },
        { id: 'groupSecurity', label: 'Security & risk', hint: 'Heuristic leads (Risky APIs) are kept apart from advisories for known vulnerable packages (Vulnerabilities)' },
        { id: 'groupCompatibility', label: 'Compatibility', hint: 'What changed in the public API between two versions, and what can break callers' },
        { id: 'groupDetail', label: 'Detailed views', hint: 'Pick a class to see what it derives from; pick a method to see who calls it, what it calls and its C# code' }
    ];

    // `standalone`: works without an assembly in the explorer (it brings its own input).
    const VIEWS = [
        { id: 'overview', group: 'groupSummary', label: 'Overview', title: 'Overview', subtitle: 'What this assembly is: identity, framework, signing and a first look at what it contains' },
        { id: 'metrics', group: 'groupAnalysis', label: 'Metrics', title: 'Code Metrics', subtitle: 'Complexity, size, callers and unreferenced methods of the loaded assembly' },
        { id: 'architecture', group: 'groupAnalysis', label: 'Architecture', title: 'Architecture', subtitle: 'How the namespaces of the loaded assembly depend on each other, and where they depend on each other in a circle' },
        { id: 'risky', group: 'groupSecurity', label: 'Risky APIs', title: 'Risky APIs', subtitle: 'Calls to APIs that deserve a security review (deserialization, processes, code loading, weak crypto, native code, TLS checks)' },
        { id: 'vulns', group: 'groupSecurity', label: 'Vulnerabilities', title: 'Vulnerabilities', subtitle: 'Known vulnerabilities (CVE) in the dependencies of the loaded assembly - Critical and High first', standalone: true },
        { id: 'compare', group: 'groupCompatibility', label: 'API Compatibility', title: 'API Compatibility', subtitle: 'Compare two versions of an assembly: what changed in the public API and what can break callers', standalone: true },
        { id: 'inheritance', group: 'groupDetail', label: 'Inheritance', title: 'Inheritance', subtitle: 'Base classes, derived classes and interfaces of the selected class' },
        { id: 'calls', group: 'groupDetail', label: 'Method Calls', title: 'Method Calls', subtitle: 'Callers and callees of the selected method' },
        { id: 'cycles', group: 'groupDetail', label: 'Circular Dependencies', title: 'Circular Dependencies', subtitle: 'Groups of methods that call each other in a circle' },
        { id: 'source', group: 'groupDetail', label: 'C# Source', title: 'C# Source', subtitle: 'Decompiled C# of the selected type or method' }
    ];

    const cap = (id) => id.charAt(0).toUpperCase() + id.slice(1);

    return {
        GROUPS, VIEWS,
        START_VIEW: 'overview',
        ids: () => VIEWS.map(v => v.id),
        get: (id) => VIEWS.find(v => v.id === id) || null,
        inGroup: (groupId) => VIEWS.filter(v => v.group === groupId),
        /** DOM ids for a view: the tab button and the section that shows it. */
        tabId: (id) => 'tab' + cap(id),
        sectionId: (id) => 'view' + cap(id),
        /** A view that can be opened before any assembly is loaded. */
        isStandalone: (id) => !!(VIEWS.find(v => v.id === id) || {}).standalone
    };
});
