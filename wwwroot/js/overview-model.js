/*
 * What the Overview page says, as plain data (no DOM): the identity facts of the assembly and the "at a glance" cards that lead to the
 * other views. Pure functions so the wording and the rules (what is shown when a fact is missing) are testable.
 */
(function (root, factory) {
    if (typeof module === 'object' && module.exports) module.exports = factory();
    else root.OverviewModel = factory();
})(typeof self !== 'undefined' ? self : this, function () {
    'use strict';

    function size(bytes) {
        if (!bytes) return null;
        return bytes < 1048576 ? (bytes / 1024).toFixed(1) + ' KB' : (bytes / 1048576).toFixed(1) + ' MB';
    }

    function framework(a) {
        if (!a.targetFramework) return 'not recorded in the file';
        return a.targetFramework + (a.targetFrameworkInferred ? ' (guessed from its references: the file does not record one)' : '');
    }

    function strongName(a) {
        if (!a.strongNamed) return 'no - the assembly is not signed with a strong name';
        const bits = a.publicKeyBits ? ', ' + a.publicKeyBits + '-bit key' : '';
        return a.publicKeyToken ? 'yes - public key token ' + a.publicKeyToken + bits : 'yes' + bits;
    }

    /** [label, value, mono?] rows; a fact the file does not have is left out instead of shown as "unknown". */
    function identityRows(a) {
        if (!a) return [];
        const rows = [
            ['Name', a.name], ['Version', a.version], ['Full name', a.fullName, true], ['Culture', a.culture],
            ['Kind', a.kind], ['Platform', a.platform], ['Target framework', framework(a)],
            ['Strong name', strongName(a)], ['Module id (MVID)', a.mvid, true],
            ['Build', a.debugBuild ? 'debug (contains debug information)' : 'release / no debug information'],
            ['Entry point', a.hasEntryPoint ? 'yes' : 'no'], ['References', a.references != null ? a.references + ' assemblies' : null], ['File size', size(a.sizeBytes)]
        ];
        return rows.filter(r => r[1] !== null && r[1] !== undefined && r[1] !== '');
    }

    const plural = (n, one, many) => n + ' ' + (n === 1 ? one : many);

    /**
     * Cards that lead to the other views. `s` = { types, methods, namespaces, cycles, risky, metrics, unreferenced, vuln }
     * where vuln is { state: 'none' | 'scanning' | 'done', open, urgent }.
     */
    function glanceCards(s) {
        const cards = [
            { key: 'types', num: s.types, label: 'Types', view: 'architecture', hint: plural(s.namespaces || 0, 'namespace', 'namespaces') + ' - open Architecture' },
            { key: 'methods', num: s.methods, label: 'Methods', view: 'metrics', hint: 'open Metrics' }
        ];
        if (s.metrics) cards.push({ key: 'complexity', num: s.metrics.averageComplexity, label: 'Average complexity', view: 'metrics', hint: 'highest ' + s.metrics.maxComplexity + ' - open Metrics', tone: s.metrics.maxComplexity > 20 ? 'warn' : '' });
        cards.push({ key: 'unreferenced', num: s.unreferenced, label: 'Unreferenced methods', view: 'metrics', hint: 'candidates for removal - open Metrics' });
        cards.push({ key: 'cycles', num: s.cycles, label: 'Circular call chains', view: 'cycles', hint: 'open Circular Dependencies', tone: s.cycles ? 'warn' : '' });
        cards.push({ key: 'risky', num: s.risky, label: 'Risky API uses', view: 'risky', hint: 'leads for a security review - open Risky APIs', tone: s.risky ? 'warn' : '' });

        const v = s.vuln || { state: 'none' };
        if (v.state === 'scanning') cards.push({ key: 'vulns', num: '…', label: 'Vulnerabilities', view: 'vulns', hint: 'scanning in the background', tone: 'busy' });
        else if (v.state === 'done') cards.push({ key: 'vulns', num: v.open, label: 'Vulnerabilities', view: 'vulns', hint: v.urgent ? v.urgent + ' critical / high - open Vulnerabilities' : 'open Vulnerabilities', tone: v.urgent ? 'bad' : '' });
        else cards.push({ key: 'vulns', num: '-', label: 'Vulnerabilities', view: 'vulns', hint: 'not scanned yet - open Vulnerabilities' });
        return cards;
    }

    return { identityRows, glanceCards, size, framework, strongName };
});
