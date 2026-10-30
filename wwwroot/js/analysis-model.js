/*
 * The browser-side model of one analysed assembly: lookup tables over the analyzer's JSON, the de-duplicated circular chains, and
 * the navigation rules that connect the views (which view a selection opens, which method is part of which cycle).
 * Pure functions, no DOM; runs in the browser (window.AnalysisModel) and under node --test.
 */
(function (root, factory) {
    if (typeof module === 'object' && module.exports) module.exports = factory();
    else root.AnalysisModel = factory();
})(typeof self !== 'undefined' ? self : this, function () {
    'use strict';

    const SEP = '\u0001';

    function shortName(fullName) {
        if (!fullName) return '';
        const i = fullName.lastIndexOf('.');
        return i < 0 ? fullName : fullName.substring(i + 1);
    }

    /** The same cycle written from any of its methods gets the same key (the lexicographically smallest rotation). */
    function canonicalKey(body) {
        let best = null;
        for (let i = 0; i < body.length; i++) {
            const k = body.slice(i).concat(body.slice(0, i)).join(SEP);
            if (best === null || k < best) best = k;
        }
        return best;
    }

    /** Builds every lookup table the views use from the analyzer's JSON. Missing sections are treated as empty. */
    function build(data) {
        const d = data || {};
        const inheritance = d.inheritance || { nodes: [], edges: [] };
        const graph = d.methodGraph || { nodes: [], edges: [] };
        const m = {
            typeNodes: new Map(), basesOf: new Map(), derivedOf: new Map(), nsOf: new Map(), typesByNs: new Map(),
            methodById: new Map(), methodsByClass: new Map(), calleesOf: new Map(), callersOf: new Map(), edgeCounts: new Map(),
            cycles: [], cyclesOfMethod: new Map(), cyclicEdges: new Set()
        };

        (inheritance.nodes || []).forEach(n => m.typeNodes.set(n.id, n));
        (d.classes || []).forEach(fullName => {
            if (!m.typeNodes.has(fullName)) m.typeNodes.set(fullName, { id: fullName, name: shortName(fullName), type: 'Class' });
            const node = m.typeNodes.get(fullName);
            let ns;
            if (typeof node.namespace === 'string') {
                // the analyzer says which namespace the type is in; a nested type shows as Outer.Inner inside that namespace
                ns = node.namespace.trim();
                if (node.nested && ns && fullName.startsWith(ns + '.')) node.name = fullName.substring(ns.length + 1);
                else if (node.nested && !ns) node.name = fullName;
            } else {
                // older analysis without the field: best effort from the name (wrong for nested types)
                const parts = fullName.split('.');
                parts.pop();
                ns = parts.join('.').trim();
            }
            m.nsOf.set(fullName, ns);
            if (!m.typesByNs.has(ns)) m.typesByNs.set(ns, []);
            m.typesByNs.get(ns).push(fullName);
        });
        m.typesByNs.forEach(list => list.sort((a, b) => m.typeNodes.get(a).name.localeCompare(m.typeNodes.get(b).name)));

        (inheritance.edges || []).forEach(e => {
            if (e.to === 'GenericTypeSpecification') return;      // placeholder written by older analyzers for generic instantiations
            if (!m.basesOf.has(e.from)) m.basesOf.set(e.from, []);
            if (!m.derivedOf.has(e.to)) m.derivedOf.set(e.to, []);
            if (!m.basesOf.get(e.from).some(x => x.id === e.to)) m.basesOf.get(e.from).push({ id: e.to, relation: e.relation });
            if (!m.derivedOf.get(e.to).some(x => x.id === e.from)) m.derivedOf.get(e.to).push({ id: e.from, relation: e.relation });
        });

        (graph.nodes || []).forEach(n => {
            m.methodById.set(n.id, n);
            if (!m.methodsByClass.has(n.parentClass)) m.methodsByClass.set(n.parentClass, []);
            m.methodsByClass.get(n.parentClass).push(n);
        });
        m.methodsByClass.forEach(list => list.sort((a, b) => a.name.localeCompare(b.name)));

        // duplicate call edges become one edge with a count
        (graph.edges || []).forEach(e => {
            const key = e.from + SEP + e.to;
            m.edgeCounts.set(key, (m.edgeCounts.get(key) || 0) + 1);
            if (m.edgeCounts.get(key) === 1) {
                if (!m.calleesOf.has(e.from)) m.calleesOf.set(e.from, []);
                if (!m.callersOf.has(e.to)) m.callersOf.set(e.to, []);
                m.calleesOf.get(e.from).push(e.to);
                m.callersOf.get(e.to).push(e.from);
            }
        });

        // circular chains: drop duplicates of the same cycle that merely start at a different method
        const seen = new Set();
        (d.deadlocks || []).forEach(raw => {
            if (!raw || raw.length === 0) return;
            const body = raw.length > 1 && raw[0] === raw[raw.length - 1] ? raw.slice(0, -1) : raw.slice();
            const key = canonicalKey(body);
            if (seen.has(key)) return;
            seen.add(key);
            const idx = m.cycles.length;
            m.cycles.push({ body });
            body.forEach((id, i) => {
                if (!m.cyclesOfMethod.has(id)) m.cyclesOfMethod.set(id, []);
                m.cyclesOfMethod.get(id).push(idx);
                m.cyclicEdges.add(id + SEP + body[(i + 1) % body.length]);
            });
        });
        return m;
    }

    /**
     * What Method View tells the user about circular dependencies for a method.
     * Returns null when the method is in no cycle, else { count, first, others } where `first` is the cycle to open.
     */
    function cycleInfo(model, methodId) {
        const list = (model.cyclesOfMethod.get(methodId) || []);
        if (!list.length) return null;
        return { count: list.length, first: list[0], others: list.slice(1) };
    }

    /** The ILSpy-style rule: choosing a class shows its inheritance, choosing a method shows its call graph. */
    function viewForSelection(kind) {
        return kind === 'method' ? 'calls' : 'inheritance';
    }

    /** Methods of one cycle, in the order the chain runs, with the edge that closes it. */
    function describeCycle(model, index) {
        const c = model.cycles[index];
        if (!c) return null;
        return { methods: c.body.slice(), closesWith: c.body[0], length: c.body.length, classes: [...new Set(c.body.map(id => (model.methodById.get(id) || {}).parentClass).filter(Boolean))] };
    }

    /** Plain-language reading of the analysis diagnostics ('' when there is nothing to say). */
    function describeDiagnostics(d) {
        if (!d) return '';
        const parts = [];
        if (d.referenceAssembly) parts.push('this is a reference assembly (the API only, compiled without method bodies), so there is no real code to analyse - call graphs and metrics will be empty or meaningless');
        const skipped = (d.skippedTypes || 0) + (d.skippedMethods || 0) + (d.skippedInterfaces || 0);
        if (skipped || d.unreadableBodies) {
            const n = (count, word) => count + ' ' + word + (count === 1 ? '' : 's');
            const bits = [];
            if (d.skippedTypes) bits.push(n(d.skippedTypes, 'type'));
            if (d.skippedMethods) bits.push(n(d.skippedMethods, 'method'));
            if (d.skippedInterfaces) bits.push(n(d.skippedInterfaces, 'interface relation'));
            if (d.unreadableBodies) bits.push('the code of ' + n(d.unreadableBodies, 'method'));
            parts.push('could not read ' + bits.join(', ') + ' (damaged, packed or protected metadata?), so the results are incomplete');
        }
        return parts.length ? parts.join('; ') + '.' : '';
    }

    /** One sentence when the analysis found signs of obfuscation, else ''. The detail and the evidence are shown in the Metrics view. */
    function describeObfuscation(o) {
        if (!o || !o.likely) return '';
        return 'this assembly shows signs of obfuscation (' + (o.evidence || []).join('; ') + '). Names are shown as they are in the file, and the call graph may be incomplete - see Metrics > Obfuscation indicators.';
    }

    return { SEP, shortName, canonicalKey, build, cycleInfo, viewForSelection, describeCycle, describeDiagnostics, describeObfuscation };
});
