/*
 * When the vulnerability scan starts by itself. Pure functions, no DOM: the decision is testable and the same code is used
 * wherever the scan can be triggered (an assembly is opened, selected, or its manifest arrives).
 */
(function (root, factory) {
    if (typeof module === 'object' && module.exports) module.exports = factory();
    else root.ScanPolicy = factory();
})(typeof self !== 'undefined' ? self : this, function () {
    'use strict';

    const RESULT_VALID_MS = 60 * 60 * 1000;       // a finished scan is reused for an hour (advisories change slowly; "Re-run scan" always scans)
    const RETRY_AFTER_FAILURE_MS = 2 * 60 * 1000; // after a failed or incomplete scan, do not retry by itself for two minutes
    const SETTLE_MS = 400;                        // wait for the rest of an opened folder / package (its manifest) before scanning

    /**
     * Identifies what a scan was run on: the file (name, size, last modified), the manifest if any, and the option that adds this tool's
     * own packages. The same key means the same scan input; a changed file (same name, new build) or a new manifest is a different key.
     */
    function scanKey(file, depsFile, includeApp) {
        const part = (f) => f ? [f.name, f.size, f.lastModified].join(':') : '-';
        return [part(file), part(depsFile), includeApp ? 'app' : 'no-app'].join('|');
    }

    /**
     * Decides whether to start a scan now. Returns { run, reason }; the reason is one of
     *   run | disabled | server-off | no-consent | no-assembly | already-scanning | fresh-result | recent-failure
     */
    function decideAutoScan(s) {
        if (!s.hasAssembly) return { run: false, reason: 'no-assembly' };
        if (s.serverEnabled === false) return { run: false, reason: 'server-off' };
        if (!s.autoEnabled) return { run: false, reason: 'disabled' };
        if (!s.consent) return { run: false, reason: 'no-consent' };
        if (s.scanning) return { run: false, reason: 'already-scanning' };

        const age = s.now - (s.lastFinishedAt || 0);
        if (s.report && s.reportKey === s.key && (s.report.databaseReachable || s.report.nothingToCheck) && age < RESULT_VALID_MS && s.lastOutcome !== 'incomplete')
            return { run: false, reason: 'fresh-result' };
        if (s.lastKey === s.key && (s.lastOutcome === 'failed' || s.lastOutcome === 'incomplete') && age < RETRY_AFTER_FAILURE_MS)
            return { run: false, reason: 'recent-failure' };
        return { run: true, reason: 'run' };
    }

    /** One line for the status bar / toast when a scan finishes. */
    function describeResult(name, report) {
        if (!report) return 'The scan of ' + name + ' did not finish.';
        const s = report.summary || { total: 0, critical: 0, high: 0 };
        if (report.nothingToCheck && report.complete) return name + ': no NuGet packages to check.';
        if (!report.databaseReachable && !report.nothingToCheck) return name + ': the vulnerability database could not be reached - not a clean result.';
        if (s.total === 0) return name + ': no known vulnerabilities' + (report.complete ? '.' : ' found, but the scan was incomplete.');
        const urgent = (s.critical || 0) + (s.high || 0);
        return name + ': ' + s.total + ' finding' + (s.total === 1 ? '' : 's') + (urgent ? ' (' + urgent + ' critical / high)' : '') + (report.complete ? '.' : ', scan incomplete.');
    }

    return { RESULT_VALID_MS, RETRY_AFTER_FAILURE_MS, SETTLE_MS, scanKey, decideAutoScan, describeResult };
});
