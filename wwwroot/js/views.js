/*
 * The list of views and the groups they are shown in, in one place. The tab bar, the "which view is active" logic and the tests read it,
 * so a view is added, moved to another group or merged into another screen here instead of in several files.
 *
 * The groups follow the workflow: open a DLL -> Overview -> Details -> Security & risk -> Compare.
 * The id names the tab and, unless the view lists `sections`, the element that shows it (tab<Id>, view<Id>).
 */
(function (root, factory) {
    if (typeof module === 'object' && module.exports) module.exports = factory();
    else root.ViewRegistry = factory();
})(typeof self !== 'undefined' ? self : this, function () {
    'use strict';

    const GROUPS = [
        { id: 'groupSummary', label: 'Overview', hint: 'Start here: what this assembly is, how its namespaces depend on each other and how big and complex it is' },
        { id: 'groupDetail', label: 'Details', hint: 'Pick a class to see what it derives from; pick a method to see who calls it, what it calls and its C# code' },
        { id: 'groupSecurity', label: 'Security & risk', hint: 'Heuristic leads (Risky APIs) are kept apart from advisories for known vulnerable packages (Vulnerabilities)' },
        { id: 'groupCompare', label: 'Compare', hint: 'What changed in the public API between two versions, and what can break callers' }
    ];

    // `standalone`: works without an assembly in the explorer (it brings its own input, or explains how to start).
    const VIEWS = [
        // one tab, three screens: the summary, the namespace architecture and the metrics (a switch at the top of the page)
        { id: 'overview', group: 'groupSummary', label: 'Overview', title: 'Overview', subtitle: 'What this assembly is: identity, framework, signing and a first look at what it contains', standalone: true,
          sections: ['viewOverview', 'viewArchitecture', 'viewMetrics'] },
        // one tab, two screens: a selected class shows its inheritance, a selected method shows its calls - each with its C# code below
        { id: 'navigator', group: 'groupDetail', label: 'Code Navigator', title: 'Code Navigator', subtitle: 'The selected class (inheritance) or method (callers and callees) with its C# code', sections: ['viewInheritance', 'viewCalls'] },
        { id: 'cycles', group: 'groupDetail', label: 'Circular Dependencies', title: 'Circular Dependencies', subtitle: 'Groups of methods that call each other in a circle' },
        { id: 'risky', group: 'groupSecurity', label: 'Risky APIs', title: 'Risky APIs', subtitle: 'Calls to APIs that deserve a security review (deserialization, processes, code loading, weak crypto, native code, TLS checks)' },
        { id: 'vulns', group: 'groupSecurity', label: 'Vulnerabilities', title: 'Vulnerabilities', subtitle: 'Known vulnerabilities (CVE) in the dependencies of the loaded assembly - Critical and High first', standalone: true },
        { id: 'compare', group: 'groupCompare', label: 'Compare Versions', title: 'Compare Versions', subtitle: 'Compare two versions of an assembly: what changed in the public API and what can break callers', standalone: true }
    ];

    // The screens inside the Overview tab. Older links to 'architecture' and 'metrics' still work: they open these.
    const OVERVIEW_TABS = [
        { key: 'summary', label: 'Summary', section: 'viewOverview' },
        { key: 'architecture', label: 'Architecture', section: 'viewArchitecture' },
        { key: 'metrics', label: 'Metrics', section: 'viewMetrics' }
    ];

    const cap = (id) => id.charAt(0).toUpperCase() + id.slice(1);

    return {
        GROUPS, VIEWS, OVERVIEW_TABS,
        START_VIEW: 'overview',
        ids: () => VIEWS.map(v => v.id),
        get: (id) => VIEWS.find(v => v.id === id) || null,
        inGroup: (groupId) => VIEWS.filter(v => v.group === groupId),
        /** DOM ids for a view: the tab button and the section that shows it. */
        tabId: (id) => 'tab' + cap(id),
        sectionId: (id) => 'view' + cap(id),
        /** Every page section a view owns (most views have exactly one). */
        sections: (id) => { const v = VIEWS.find(x => x.id === id); return v && v.sections ? v.sections : ['view' + cap(id)]; },
        /** A view name as used in links -> { view, tab }: 'architecture' and 'metrics' are screens of the Overview. */
        resolve: (id) => OVERVIEW_TABS.some(t => t.key === id && id !== 'summary') ? { view: 'overview', tab: id } : { view: id },
        overviewSection: (tab) => (OVERVIEW_TABS.find(t => t.key === tab) || OVERVIEW_TABS[0]).section,
        /** A view that can be opened before any assembly is loaded. */
        isStandalone: (id) => !!(VIEWS.find(v => v.id === id) || {}).standalone
    };
});
