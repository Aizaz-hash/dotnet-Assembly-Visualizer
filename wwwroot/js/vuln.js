// ============================================================================
//  Vulnerabilities view
//  Calls POST /api/vulnerabilities/scan (read-only lookups against OSV.dev) and shows the result.
//  Reuses the helpers from app.js (byId, el, navigate, showMessage, ...).
// ============================================================================

const SEVERITIES = ['Critical', 'High', 'Medium', 'Low', 'Unknown'];
const VULN_STATUSES = ['Open', 'Accepted Risk', 'Fixed'];
const VULN_STATUS_KEY = 'dav.vuln.status';
const VULN_CONSENT_KEY = 'dav.vuln.consent';

let vulnReport = null;
let vulnInfo = null;                        // { enabled, host, sends } from /status
let vulnBusy = false;
let vulnDepsFile = null;
const vulnReports = new Map();              // assembly name -> report (a scan belongs to the assembly it was run for)
const vulnDepsFiles = new Map();            // assembly name -> chosen .deps.json
let vulnSelectedKey = null;
const vulnFilters = { severities: new Set(), component: '', status: '', confidence: '', text: '' };

// ---------- persistence of user decisions (Accepted Risk / Fixed) ----------

function vulnStore() {
    try { return JSON.parse(localStorage.getItem(VULN_STATUS_KEY) || '{}'); } catch { return {}; }
}
function vulnSaveStore(store) {
    try { localStorage.setItem(VULN_STATUS_KEY, JSON.stringify(store)); } catch { /* storage unavailable */ }
}
function vulnKey(f) { return `${f.databaseId}|${f.ecosystem}|${f.component}@${f.installedVersion}`; }

function vulnStatusOf(f) { return f.userStatus || 'Open'; }

function applyStoredStatuses(report) {
    const store = vulnStore();
    let changed = false;
    report.findings.forEach(f => {
        const saved = store[vulnKey(f)];
        f.userStatus = 'Open';
        if (!saved) return;
        if (saved.status === 'Fixed') {
            // The user said it was fixed, but the scan still reports it: it is open again.
            f.reopened = true;
            delete store[vulnKey(f)];
            changed = true;
        } else {
            f.userStatus = saved.status;
            f.statusDate = saved.date;
        }
    });
    if (changed) vulnSaveStore(store);
}

function setFindingStatus(f, status) {
    const store = vulnStore();
    if (status === 'Open') delete store[vulnKey(f)];
    else store[vulnKey(f)] = { status, date: new Date().toISOString() };
    vulnSaveStore(store);
    f.userStatus = status;
    f.statusDate = status === 'Open' ? undefined : new Date().toISOString();
    f.reopened = false;
    renderVulns();
    updateVulnBadge();
}

// ---------- scan ----------

async function loadVulnInfo() {
    try {
        const r = await fetch('/api/vulnerabilities/status');
        if (r.ok) vulnInfo = await r.json();
    } catch { /* shown as "unknown" */ }
}

function vulnConsent() {
    try { if (localStorage.getItem(VULN_CONSENT_KEY) === 'yes') return true; } catch { /* ignore */ }
    const host = (vulnInfo && vulnInfo.host) || 'api.osv.dev';
    const ok = window.confirm(
        'Vulnerability scan\n\nThis sends package names and versions (never your code or assemblies) to ' + host + ' (OSV.dev) ' +
        'and, to find each package\'s own dependencies, to ' + ((vulnInfo && vulnInfo.dependencyHost) || 'api.nuget.org') + ' (NuGet). ' +
        'It only reads the answers. Nothing is installed or changed.\n\nContinue?');
    if (ok) { try { localStorage.setItem(VULN_CONSENT_KEY, 'yes'); } catch { /* ignore */ } }
    return ok;
}

// ---------- per-assembly scope ----------
// A scan always belongs to the loaded assembly: it is remembered per assembly, shown only while that assembly is
// active, and forgotten when the assembly is removed.

function vulnOnAssemblyChanged() {
    vulnReport = hasAssembly ? (vulnReports.get(currentAssemblyName) || null) : null;
    vulnDepsFile = hasAssembly ? (vulnDepsFiles.get(currentAssemblyName) || null) : null;
    vulnSelectedKey = vulnReport && vulnReport.findings.length ? vulnKey(vulnReport.findings[0]) : null;
    vulnFilters.severities.clear();
    vulnFilters.component = vulnFilters.status = vulnFilters.confidence = vulnFilters.text = '';
    syncVulnToolbar();
    updateVulnBadge();
}

// A .deps.json that came with an opened package / folder: used for this assembly's scan (exact versions).
function vulnSetDeps(name, file) {
    vulnDepsFiles.set(name, file);
    if (hasAssembly && currentAssemblyName === name) { vulnDepsFile = file; syncVulnToolbar(); }
}

function vulnForgetAssembly(name) {
    vulnReports.delete(name);
    vulnDepsFiles.delete(name);
}

function syncVulnToolbar() {
    const has = hasAssembly;
    byId('btnVulnScan').disabled = !has || vulnBusy;
    byId('btnVulnScan').textContent = vulnBusy ? 'Scanning…' : (vulnReport ? 'Re-run scan' : 'Run scan');
    byId('btnVulnScan').title = has ? 'Scan the dependencies of ' + currentAssemblyName : 'Open an assembly first';
    byId('vulnOptApp').disabled = !has;
    byId('btnVulnDeps').disabled = !has;
    byId('btnVulnExport').disabled = !vulnReport;
    byId('btnVulnPdf').disabled = !vulnReport;
    byId('vulnDepsName').textContent = vulnDepsFile ? vulnDepsFile.name : '';
    byId('btnVulnDepsClear').style.display = vulnDepsFile ? 'inline-block' : 'none';
    byId('vulnScope').textContent = has ? 'Scanning: ' + currentAssemblyName : 'No assembly loaded';
}

async function runVulnScan() {
    if (vulnBusy) return;
    if (!hasAssembly) { showMessage('Open an assembly first - the scan is run for the loaded assembly.', 'error'); return; }
    const name = currentAssemblyName;
    const includeApp = byId('vulnOptApp').checked;
    if (!vulnConsent()) return;

    vulnBusy = true;
    syncVulnToolbar();
    setStatus('Resolving dependencies of ' + name + ' and checking them against OSV.dev…');

    const form = new FormData();
    form.append('includeApplication', includeApp ? 'true' : 'false');
    if (vulnDepsFile) form.append('depsFile', vulnDepsFile);
    form.append('assemblyName', (rawAssemblyData.assemblyName || name).replace(/\.(dll|exe)$/i, ''));
    if (rawAssemblyData.targetFramework) form.append('targetFramework', rawAssemblyData.targetFramework);
    if (rawAssemblyData.references && rawAssemblyData.references.length)
        form.append('references', JSON.stringify(rawAssemblyData.references));

    try {
        const response = await fetch('/api/vulnerabilities/scan', { method: 'POST', body: form });
        if (!response.ok) throw new Error((await response.text()) || ('Server returned ' + response.status));
        const report = await response.json();
        report.scannedFor = name;
        report.scannedSources = ['the assembly\'s own references (dependency levels 1-' + (report.maxLevelResolved || 1) + ')', vulnDepsFile ? vulnDepsFile.name : null, includeApp ? 'this tool\'s own packages' : null].filter(Boolean);
        applyStoredStatuses(report);
        if (loadedAssemblies.has(name)) vulnReports.set(name, report);      // the assembly may have been removed meanwhile
        const s = report.summary;
        setStatus('Scan of ' + name + ' finished: ' + s.total + ' finding' + (s.total === 1 ? '' : 's') + ' (' + s.critical + ' critical, ' + s.high + ' high)' + (report.complete ? '' : ' - INCOMPLETE'));
    } catch (err) {
        showMessage('Scan failed: ' + err.message, 'error');
        setStatus('Ready');
    } finally {
        vulnBusy = false;
        if (hasAssembly && currentAssemblyName === name) vulnOnAssemblyChanged();   // show this assembly's (new) report
        else syncVulnToolbar();
        renderVulns();
    }
}

function updateVulnBadge() {
    const badge = byId('vulnBadge');
    if (!vulnReport) { setTabBadge(badge, 0, 'Not scanned yet'); return; }
    const open = vulnReport.findings.filter(f => vulnStatusOf(f) === 'Open');
    const urgent = open.filter(f => f.severity === 'Critical' || f.severity === 'High').length;
    const title = open.length === 0
        ? (vulnReport.complete ? 'No open vulnerabilities' : 'No vulnerabilities reported, but the scan was incomplete')
        : open.length + ' open vulnerabilit' + (open.length === 1 ? 'y' : 'ies') + (urgent ? ' (' + urgent + ' critical / high)' : '');
    setTabBadge(badge, open.length, title);
}

function exportVulnReport() {
    if (!vulnReport) return;
    const blob = new Blob([JSON.stringify(vulnReport, null, 2)], { type: 'application/json' });
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = 'vulnerability-report-' + vulnReport.scannedAtUtc.slice(0, 10) + '.json';
    a.click();
    setTimeout(() => URL.revokeObjectURL(a.href), 2000);
}

// ---------- PDF export (browser print -> "Save as PDF"; no extra library) ----------

const PDF_CSS = `
@page { size: A4; margin: 14mm 12mm; }
* { box-sizing: border-box; }
body { font-family: "Segoe UI", Arial, sans-serif; font-size: 10.5pt; color: #212529; margin: 0; -webkit-print-color-adjust: exact; print-color-adjust: exact; }
h1 { font-size: 20pt; margin: 0 0 2mm; } h2 { font-size: 13pt; margin: 7mm 0 2mm; border-bottom: 1px solid #adb5bd; padding-bottom: 1mm; }
.meta { color: #495057; font-size: 9.5pt; line-height: 1.5; }
.banner { border: 1px solid; border-radius: 3px; padding: 2.5mm 3.5mm; margin: 4mm 0; font-size: 10pt; }
.banner.ok { background: #d4edda; color: #155724; border-color: #c3e6cb; } .banner.warn { background: #fff3cd; color: #856404; border-color: #ffeeba; } .banner.bad { background: #f8d7da; color: #721c24; border-color: #f5c6cb; }
.banner ul { margin: 1.5mm 0 0; padding-left: 5mm; }
.cards { display: flex; gap: 3mm; margin: 3mm 0; }
.card { flex: 1; border: 1px solid #dee2e6; border-left: 4px solid #adb5bd; padding: 2mm 3mm; } .card b { display: block; font-size: 18pt; } .card span { font-size: 8.5pt; text-transform: uppercase; color: #495057; }
.card.critical { border-left-color: #721c24; } .card.high { border-left-color: #dc3545; } .card.medium { border-left-color: #fb8c00; } .card.low { border-left-color: #1e88e5; } .card.unknown { border-left-color: #6c757d; }
table { width: 100%; border-collapse: collapse; font-size: 9pt; } th { background: #f1f3f5; text-align: left; padding: 1.5mm 2mm; border-bottom: 1px solid #adb5bd; } td { padding: 1.5mm 2mm; border-bottom: 1px solid #e9ecef; vertical-align: top; }
tr { page-break-inside: avoid; }
.sev { display: inline-block; padding: 0 2.5mm; border-radius: 3mm; color: #fff; font-weight: 700; font-size: 8.5pt; white-space: nowrap; }
.sev.critical { background: #721c24; } .sev.high { background: #dc3545; } .sev.medium { background: #fb8c00; } .sev.low { background: #1e88e5; } .sev.unknown { background: #6c757d; }
.finding { border: 1px solid #dee2e6; border-radius: 2mm; padding: 3mm 4mm; margin: 3mm 0; page-break-inside: avoid; }
.finding h3 { margin: 0 0 1.5mm; font-size: 11.5pt; } .finding .sum { margin-bottom: 2mm; }
.finding dl { display: grid; grid-template-columns: 30mm 1fr; gap: 0.8mm 3mm; margin: 0 0 2mm; font-size: 9pt; } .finding dt { color: #6c757d; } .finding dd { margin: 0; word-break: break-word; }
.finding h4 { margin: 2mm 0 0.5mm; font-size: 9.5pt; } .finding ul { margin: 0.5mm 0 0; padding-left: 5mm; font-size: 9pt; }
.conf { border-radius: 2mm; padding: 1.5mm 2.5mm; font-size: 9pt; margin: 1.5mm 0; } .conf.confirmed { background: #d4edda; color: #155724; } .conf.potential { background: #fff3cd; color: #856404; }
code { font-family: Consolas, monospace; background: #f1f3f5; padding: 0 1mm; }
.foot { margin-top: 6mm; font-size: 8.5pt; color: #6c757d; border-top: 1px solid #dee2e6; padding-top: 2mm; }
`;

function buildVulnReportDocument(doc, report) {
    const E = (tag, cls, text) => { const n = doc.createElement(tag); if (cls) n.className = cls; if (text !== undefined) n.textContent = text; return n; };
    const body = doc.body;
    body.innerHTML = '';
    const style = doc.createElement('style'); style.textContent = PDF_CSS; doc.head.appendChild(style);
    doc.title = 'Vulnerabilities Scan - ' + (report.scannedFor || 'report');

    body.appendChild(E('h1', null, 'Vulnerabilities Scan report'));
    const meta = E('div', 'meta');
    [
        ['Assembly', report.scannedFor || '-'],
        ['Sources', (report.scannedSources && report.scannedSources.length ? report.scannedSources.join(', ') : '-')],
        ['Database', report.database + ' (' + (report.nothingToCheck ? 'not queried - nothing to check' : (report.databaseReachable ? 'reachable' : 'unreachable')) + ')'],
        ['Scanned', new Date(report.scannedAtUtc).toLocaleString()],
        ['Components checked', String(report.components.length)]
    ].forEach(([k, v]) => { const line = E('div'); line.appendChild(E('b', null, k + ': ')); line.appendChild(doc.createTextNode(v)); meta.appendChild(line); });
    body.appendChild(meta);

    const ok = report.complete && report.databaseReachable;
    const banner = E('div', 'banner ' + (ok ? 'ok' : (report.databaseReachable ? 'warn' : 'bad')));
    banner.appendChild(E('b', null, report.nothingToCheck ? (report.complete ? 'No NuGet packages to check - every dependency is supplied by the .NET runtime' : 'Nothing could be checked - see the notes; this is NOT a clean result') : ok ? 'Scan complete' : (report.databaseReachable ? 'Scan INCOMPLETE - some components could not be checked' : 'Database unreachable - nothing was checked; this is NOT a clean result')));
    if (report.notes.length) { const ul = E('ul'); report.notes.forEach(n => ul.appendChild(E('li', null, n))); banner.appendChild(ul); }
    body.appendChild(banner);

    const open = report.findings.filter(f => vulnStatusOf(f) === 'Open');
    const cards = E('div', 'cards');
    SEVERITIES.forEach(sev => {
        const c = E('div', 'card ' + sev.toLowerCase());
        c.appendChild(E('b', null, String(open.filter(f => f.severity === sev).length)));
        c.appendChild(E('span', null, sev + ' (open)'));
        cards.appendChild(c);
    });
    body.appendChild(cards);

    body.appendChild(E('h2', null, 'Findings (' + report.findings.length + ')'));
    if (!report.findings.length) {
        body.appendChild(E('p', null, ok ? 'No known vulnerabilities were found for the scanned components.' : 'No vulnerabilities were reported, but the scan is incomplete (see above).'));
    } else {
        const table = E('table');
        const head = E('tr'); ['Severity', 'ID', 'Component', 'Fix', 'Status', 'Confidence'].forEach(h => head.appendChild(E('th', null, h)));
        const thead = E('thead'); thead.appendChild(head); table.appendChild(thead);
        const tb = E('tbody');
        report.findings.forEach(f => {
            const tr = E('tr');
            const t1 = E('td'); t1.appendChild(E('span', 'sev ' + f.severity.toLowerCase(), f.severity + (f.cvssScore != null ? ' ' + f.cvssScore.toFixed(1) : ''))); tr.appendChild(t1);
            tr.appendChild(E('td', null, f.id)); tr.appendChild(E('td', null, f.component + ' ' + f.installedVersion));
            tr.appendChild(E('td', null, f.recommendedVersion ? '>= ' + f.recommendedVersion : 'none listed'));
            tr.appendChild(E('td', null, vulnStatusOf(f))); tr.appendChild(E('td', null, f.confidence));
            tb.appendChild(tr);
        });
        table.appendChild(tb); body.appendChild(table);

        body.appendChild(E('h2', null, 'Details and remediation'));
        report.findings.forEach(f => {
            const box = E('div', 'finding');
            const h = E('h3'); h.appendChild(E('span', 'sev ' + f.severity.toLowerCase(), f.severity + (f.cvssScore != null ? ' ' + f.cvssScore.toFixed(1) : '')));
            h.appendChild(doc.createTextNode('  ' + f.id + ' - ' + f.component + ' ' + f.installedVersion)); box.appendChild(h);
            box.appendChild(E('div', 'sum', f.summary));
            const dl = E('dl');
            const row = (k, v) => { dl.appendChild(E('dt', null, k)); dl.appendChild(E('dd', null, v)); };
            if (f.aliases.length > 1) row('Also known as', f.aliases.filter(a => a !== f.id).join(', '));
            if (f.cwes.length) row('Weakness', f.cwes.join(', '));
            row('Severity basis', f.severitySource);
            if (f.cvssVector) row('CVSS vector', f.cvssVector);
            row('Fixed versions', f.fixedVersions.length ? f.fixedVersions.join(', ') : 'none listed');
            row('Status', vulnStatusOf(f) + (f.statusDate ? ' (since ' + new Date(f.statusDate).toLocaleDateString() + ')' : ''));
            box.appendChild(dl);
            const conf = E('div', 'conf ' + f.confidence); conf.appendChild(E('b', null, (f.confidence === 'confirmed' ? 'Confirmed: ' : 'Potential: '))); conf.appendChild(doc.createTextNode(f.confidenceNote)); box.appendChild(conf);
            const list = (title, items) => { box.appendChild(E('h4', null, title)); const ul = E('ul'); items.forEach(i => ul.appendChild(E('li', null, i))); box.appendChild(ul); };
            list('Potential impact (derived from the CVSS vector)', f.impact);
            list('Recommended remediation (nothing was changed automatically)', f.remediation);
            list('References', [f.databaseUrl].concat(f.references.slice(0, 5).map(r => r.url)));
            body.appendChild(box);
        });
    }

    body.appendChild(E('h2', null, 'Scanned components (' + report.components.length + ')'));
    const ct = E('table');
    const ch = E('tr'); ['Level', 'Package', 'Version', 'Version from', 'Status', 'Findings', 'Reached via'].forEach(h => ch.appendChild(E('th', null, h)));
    const cth = E('thead'); cth.appendChild(ch); ct.appendChild(cth);
    const cb = E('tbody');
    report.components.forEach(c => {
        const tr = E('tr');
        [c.level == null ? '-' : 'L' + c.level, c.name, c.version || '-', versionSourceText(c), c.status || (c.result === 'ok' ? 'checked' : c.result), String(c.findings), c.via || ''].forEach(v => tr.appendChild(E('td', null, v)));
        cb.appendChild(tr);
    });
    ct.appendChild(cb); body.appendChild(ct);

    body.appendChild(E('div', 'foot', 'Generated by dotnet Assembly Visualizer. Source: ' + report.database + '. Findings reflect the database at the time of the scan; ' +
        'this report is informational - verify before acting, and re-scan regularly.'));
}

function exportVulnPdf() {
    if (!vulnReport) return;
    const frame = document.createElement('iframe');
    frame.style.cssText = 'position:fixed;right:0;bottom:0;width:0;height:0;border:0;visibility:hidden;';
    document.body.appendChild(frame);
    const doc = frame.contentDocument;
    doc.open(); doc.write('<!doctype html><html><head><meta charset="utf-8"></head><body></body></html>'); doc.close();
    buildVulnReportDocument(doc, vulnReport);
    const cleanup = () => setTimeout(() => frame.remove(), 1000);
    frame.contentWindow.addEventListener('afterprint', cleanup);
    setTimeout(() => {
        frame.contentWindow.focus();
        frame.contentWindow.print();                  // choose "Save as PDF" as the destination
        setTimeout(cleanup, 60000);
    }, 200);
    showMessage('Choose "Save as PDF" in the print dialog to create the PDF.', 'info');
}

// ---------- rendering ----------

// Where a dependency's version came from, in words.
function versionSourceText(c) {
    switch (c.versionSource) {
        case 'exact': return 'manifest (exact)';
        case 'nuspec-minimum': return 'package metadata (lowest accepted)';
        case 'assembly-estimate': return 'estimated from assembly ' + (c.assemblyVersion || 'version');
        default: return c.resolution === 'framework' ? 'runtime-supplied' : 'not identified';
    }
}

function sevClass(s) { return 'sev-' + s.toLowerCase(); }

function sevBadge(severity, score) {
    const b = el('span', 'sev-badge ' + sevClass(severity), severity + (score != null ? ' ' + score.toFixed(1) : ''));
    return b;
}

function filteredFindings() {
    if (!vulnReport) return [];
    const t = vulnFilters.text.trim().toLowerCase();
    return vulnReport.findings.filter(f => {
        if (vulnFilters.severities.size && !vulnFilters.severities.has(f.severity)) return false;
        if (vulnFilters.component && f.component !== vulnFilters.component) return false;
        if (vulnFilters.status && vulnStatusOf(f) !== vulnFilters.status) return false;
        if (vulnFilters.confidence && f.confidence !== vulnFilters.confidence) return false;
        if (t) {
            const hay = (f.aliases.join(' ') + ' ' + f.summary + ' ' + f.component).toLowerCase();
            if (!hay.includes(t)) return false;
        }
        return true;
    });
}

function renderVulns() {
    const banner = byId('vulnBanner'), summary = byId('vulnSummary'), filters = byId('vulnFilters');
    const list = byId('vulnList'), detail = byId('vulnDetail'), extras = byId('vulnExtras');
    [banner, summary, filters, list, detail, extras].forEach(n => n.innerHTML = '');
    syncVulnToolbar();

    if (!hasAssembly) { renderVulnNoAssembly(banner); return; }
    if (!vulnReport) { renderVulnIntro(banner); return; }

    renderVulnBanner(banner);
    renderVulnSummary(summary);
    renderVulnFilters(filters);

    const items = filteredFindings();
    if (!vulnReport.findings.length) {
        const ok = el('div', 'vuln-empty');
        ok.appendChild(el('div', 'vuln-empty-title', vulnReport.complete ? '✓ No known vulnerabilities found' : 'No vulnerabilities were reported - but the scan is incomplete'));
        ok.appendChild(el('div', null, vulnReport.complete
            ? 'Every scanned component was checked against OSV.dev and none has a published advisory for its exact version. New advisories appear over time, so scan again regularly.'
            : 'See the notes above: components that could not be checked are not "clean".'));
        list.appendChild(ok);
    } else if (!items.length) {
        list.appendChild(el('div', 'empty-note', 'No findings match the current filters.'));
    } else {
        list.appendChild(buildFindingsTable(items));
        const selected = items.find(f => vulnKey(f) === vulnSelectedKey) || items[0];
        vulnSelectedKey = vulnKey(selected);
        renderFindingDetail(detail, selected);
    }
    renderVulnExtras(extras);
}

function renderVulnNoAssembly(container) {
    const box = el('div', 'vuln-intro');
    box.appendChild(el('h3', null, 'Open an assembly first'));
    box.appendChild(el('p', null, 'The Vulnerabilities Scan checks the dependencies of the assembly you have loaded. Click "Open" (or drop a .dll / .exe onto the page), then come back here and run the scan.'));
    box.appendChild(el('p', 'view-hint', 'Results belong to the loaded assembly and disappear when you remove it.'));
    container.appendChild(box);
}

function renderVulnIntro(container) {
    const box = el('div', 'vuln-intro');
    box.appendChild(el('h3', null, 'Vulnerabilities Scan'));
    const enabled = !vulnInfo || vulnInfo.enabled !== false;
    box.appendChild(el('p', null, enabled
        ? 'Reads the assemblies ' + currentAssemblyName + ' references, finds the NuGet packages behind them and their dependencies (up to level 4), and checks them against the OSV.dev database (GitHub Security Advisories, NVD and others). No package.json or .deps.json is needed. Every finding shows its dependency level, CVE, CVSS score, affected component, impact and a recommended fix.'
        : 'Vulnerability scanning is switched off on this server (Vulnerability:Enabled = false).'));
    const ul = el('ul');
    [
        'Only package names and versions are sent to ' + ((vulnInfo && vulnInfo.host) || 'api.osv.dev') + ' - never your code or assemblies.',
        'The scan is read-only: nothing is installed, upgraded or modified. Fix commands are shown for you to run yourself.',
        'Findings are verified against each advisory\'s affected-version ranges and marked confirmed or potential.',
        'If the database cannot be reached, the report says so instead of showing "no vulnerabilities".',
        'Level 0 is the loaded assembly, level 1 its direct references, levels 2-4 the dependencies of those packages. Each package is listed once, at its shortest path.',
        'An assembly reference carries only an assembly version (e.g. 12.0.0.0), so the package version is estimated (lowest matching version) and findings are marked potential. Add the .deps.json for exact versions.',
        'Dependencies that cannot be resolved (private assemblies, NuGet unreachable) are listed with the reason, never silently skipped.'
    ].forEach(t => ul.appendChild(el('li', null, t)));
    box.appendChild(ul);
    box.appendChild(el('p', 'view-hint', 'Press "Run scan" to scan ' + currentAssemblyName + '. A .deps.json is optional and only makes versions exact.'));
    container.appendChild(box);
}

function renderVulnBanner(container) {
    const r = vulnReport;
    const ok = r.complete && r.databaseReachable;
    const checked = r.components.filter(c => c.result === 'ok').length;
    // nothing was sent to the database: say what that means, not that the database is down
    const kind = r.nothingToCheck ? (r.complete ? 'info' : 'warn') : (ok ? 'ok' : (r.databaseReachable ? 'warn' : 'bad'));
    const title = r.nothingToCheck ? (r.complete ? 'ℹ No NuGet packages to check' : '⚠ Nothing could be checked')
        : (ok ? '✓ Scan complete' : (r.databaseReachable ? '⚠ Scan incomplete' : '✕ Database unreachable'));
    const box = el('div', 'vuln-banner ' + kind);
    const when = new Date(r.scannedAtUtc).toLocaleString();
    box.appendChild(el('div', 'vb-title', title +
        ` - ${r.components.length} dependenc${r.components.length === 1 ? 'y' : 'ies'} listed, ${checked} checked against ${r.database} · ${when}` + (r.scannedFor ? ` · ${r.scannedFor}` : '')));
    if (r.notes.length) {
        const ul = el('ul');
        r.notes.forEach(n => ul.appendChild(el('li', null, n)));
        box.appendChild(ul);
    }
    container.appendChild(box);
}

function renderVulnSummary(container) {
    const open = vulnReport.findings.filter(f => vulnStatusOf(f) === 'Open');
    const cards = el('div', 'vuln-cards');
    SEVERITIES.forEach(sev => {
        const n = open.filter(f => f.severity === sev).length;
        const total = vulnReport.findings.filter(f => f.severity === sev).length;
        const card = el('button', 'vuln-card ' + sevClass(sev) + (vulnFilters.severities.has(sev) ? ' active' : '') + (n === 0 ? ' zero' : ''));
        card.appendChild(el('div', 'vc-count', String(n)));
        card.appendChild(el('div', 'vc-label', sev));
        if (total !== n) card.appendChild(el('div', 'vc-sub', `${total - n} accepted / fixed`));
        card.title = 'Open ' + sev.toLowerCase() + ' findings - click to filter';
        card.onclick = () => {
            if (vulnFilters.severities.has(sev)) vulnFilters.severities.delete(sev); else vulnFilters.severities.add(sev);
            renderVulns();
        };
        cards.appendChild(card);
    });
    const affected = new Set(vulnReport.findings.map(f => f.component)).size;
    const totalCard = el('div', 'vuln-card total');
    totalCard.appendChild(el('div', 'vc-count', String(vulnReport.findings.length)));
    totalCard.appendChild(el('div', 'vc-label', 'Findings'));
    totalCard.appendChild(el('div', 'vc-sub', `${affected} of ${vulnReport.components.length} components affected`));
    cards.appendChild(totalCard);
    container.appendChild(cards);
}

function renderVulnFilters(container) {
    const bar = el('div', 'vuln-filter-bar');

    const sevWrap = el('span', 'vf-group');
    sevWrap.appendChild(el('span', 'vf-label', 'Severity'));
    SEVERITIES.forEach(sev => {
        const chip = el('button', 'vf-chip ' + sevClass(sev) + (vulnFilters.severities.has(sev) ? ' active' : ''), sev);
        chip.onclick = () => { if (vulnFilters.severities.has(sev)) vulnFilters.severities.delete(sev); else vulnFilters.severities.add(sev); renderVulns(); };
        sevWrap.appendChild(chip);
    });
    bar.appendChild(sevWrap);

    const select = (label, key, options) => {
        const wrap = el('label', 'vf-group', label + ' ');
        const s = el('select');
        options.forEach(([v, t]) => { const o = el('option', null, t); o.value = v; s.appendChild(o); });
        s.value = vulnFilters[key];
        s.onchange = () => { vulnFilters[key] = s.value; renderVulns(); };
        wrap.appendChild(s);
        bar.appendChild(wrap);
    };
    const comps = Array.from(new Set(vulnReport.findings.map(f => f.component))).sort((a, b) => a.localeCompare(b));
    select('Component', 'component', [['', 'All'], ...comps.map(c => [c, c])]);
    select('Status', 'status', [['', 'All'], ...VULN_STATUSES.map(s => [s, s])]);
    select('Confidence', 'confidence', [['', 'All'], ['confirmed', 'Confirmed'], ['potential', 'Potential']]);

    const search = el('input');
    search.type = 'search'; search.placeholder = 'Search CVE, package, text…'; search.value = vulnFilters.text;
    search.oninput = () => { vulnFilters.text = search.value; renderVulnsKeepFocus(); };
    bar.appendChild(search);

    const any = vulnFilters.severities.size || vulnFilters.component || vulnFilters.status || vulnFilters.confidence || vulnFilters.text;
    if (any) {
        const clear = el('button', 'tb-btn small', 'Clear filters');
        clear.onclick = () => { vulnFilters.severities.clear(); vulnFilters.component = vulnFilters.status = vulnFilters.confidence = vulnFilters.text = ''; renderVulns(); };
        bar.appendChild(clear);
    }
    bar.appendChild(el('span', 'view-hint', `${filteredFindings().length} of ${vulnReport.findings.length} shown`));
    container.appendChild(bar);
}

function renderVulnsKeepFocus() {
    const pos = byId('vulnFilters').querySelector('input[type=search]');
    const start = pos ? pos.selectionStart : null;
    renderVulns();
    const again = byId('vulnFilters').querySelector('input[type=search]');
    if (again) { again.focus(); if (start != null) again.setSelectionRange(start, start); }
}

function buildFindingsTable(items) {
    const table = el('table', 'vuln-table');
    const head = el('tr');
    ['Severity', 'ID', 'Component', 'Level', 'Fix', 'Status', 'Confidence'].forEach(h => head.appendChild(el('th', null, h)));
    const thead = el('thead'); thead.appendChild(head); table.appendChild(thead);

    const tbody = el('tbody');
    items.forEach(f => {
        const tr = el('tr', 'vuln-row' + (vulnKey(f) === vulnSelectedKey ? ' selected' : ''));
        const c1 = el('td'); c1.appendChild(sevBadge(f.severity, f.cvssScore)); tr.appendChild(c1);

        const c2 = el('td'); c2.appendChild(el('div', 'vr-id', f.id));
        if (f.databaseId !== f.id) c2.appendChild(el('div', 'vr-sub', f.databaseId));
        c2.appendChild(el('div', 'vr-sum', f.summary)); tr.appendChild(c2);

        const c3 = el('td'); c3.appendChild(el('div', 'vr-id', f.component)); c3.appendChild(el('div', 'vr-sub', f.installedVersion)); tr.appendChild(c3);
        const lvl = el('td', null, f.level == null ? '-' : 'L' + f.level);
        if (f.dependencyPath) lvl.title = f.dependencyPath;
        tr.appendChild(lvl);
        tr.appendChild(el('td', null, f.recommendedVersion ? '≥ ' + f.recommendedVersion : 'none listed'));

        const st = vulnStatusOf(f);
        const c5 = el('td'); c5.appendChild(el('span', 'status-pill st-' + st.toLowerCase().replace(' ', '-'), f.reopened ? 'Open (re-opened)' : st)); tr.appendChild(c5);
        const c6 = el('td'); c6.appendChild(el('span', 'conf-pill ' + f.confidence, f.confidence)); tr.appendChild(c6);

        tr.onclick = () => { vulnSelectedKey = vulnKey(f); renderVulns(); };
        tbody.appendChild(tr);
    });
    table.appendChild(tbody);
    return table;
}

function section(title) {
    const s = el('div', 'vd-section');
    s.appendChild(el('div', 'vd-title', title));
    return s;
}

function bulletList(items) {
    const ul = el('ul', 'vd-list');
    items.forEach(t => ul.appendChild(el('li', null, t)));
    return ul;
}

function codeLine(text) {
    const wrap = el('div', 'vd-code');
    wrap.appendChild(el('code', null, text));
    const btn = el('button', 'tb-btn small', 'Copy');
    btn.onclick = async () => { try { await navigator.clipboard.writeText(text); btn.textContent = 'Copied'; setTimeout(() => btn.textContent = 'Copy', 1500); } catch { showMessage('Could not copy - select the text manually.', 'info'); } };
    wrap.appendChild(btn);
    return wrap;
}

function renderFindingDetail(container, f) {
    const head = el('div', 'vd-head');
    head.appendChild(sevBadge(f.severity, f.cvssScore));
    head.appendChild(el('span', 'vd-id', f.id));
    container.appendChild(head);
    container.appendChild(el('div', 'vd-summary', f.summary));

    const facts = el('div', 'vd-facts');
    const fact = (k, v) => { facts.appendChild(el('span', 'vf-k', k)); facts.appendChild(el('span', 'vf-v', v)); };
    fact('Component', `${f.component} ${f.installedVersion} (${f.ecosystem})`);
    fact('Found in', f.componentSource === 'application' ? 'this application' : f.componentSource === 'bundled-script' ? 'bundled script' : f.componentSource === 'assembly-references' ? 'the loaded assembly\'s dependencies' : 'supplied .deps.json');
    if (f.level != null) fact('Dependency level', 'Level ' + f.level + (f.level === 1 ? ' (referenced directly)' : ' (transitive)') + (f.dependencyPath ? ' - ' + f.dependencyPath : ''));
    if (f.aliases.length > 1) fact('Also known as', f.aliases.filter(a => a !== f.id).join(', '));
    if (f.cwes.length) fact('Weakness', f.cwes.join(', '));
    fact('Severity basis', f.severitySource);
    if (f.cvssVector) fact('CVSS vector', f.cvssVector);
    fact('Fixed versions', f.fixedVersions.length ? f.fixedVersions.join(', ') : 'none listed');
    if (f.published) fact('Published', new Date(f.published).toLocaleDateString());
    container.appendChild(facts);

    const conf = el('div', 'vd-conf ' + f.confidence);
    conf.appendChild(el('b', null, f.confidence === 'confirmed' ? 'Confirmed: ' : 'Potential: '));
    conf.appendChild(document.createTextNode(f.confidenceNote));
    container.appendChild(conf);

    // status control (user decision; never changes anything on disk)
    const status = section('Status');
    const sel = el('select');
    VULN_STATUSES.forEach(s => { const o = el('option', null, s); o.value = s; sel.appendChild(o); });
    sel.value = vulnStatusOf(f);
    sel.onchange = () => setFindingStatus(f, sel.value);
    status.appendChild(sel);
    let hint = 'Open = needs attention. Accepted Risk = you decided to live with it (remembered in this browser). Fixed = you upgraded; the next scan confirms it.';
    if (f.reopened) hint = 'You marked this as fixed, but the latest scan still reports it, so it is open again.';
    else if (f.statusDate && vulnStatusOf(f) !== 'Open') hint = 'Set on ' + new Date(f.statusDate).toLocaleDateString() + '. ' + hint;
    status.appendChild(el('div', 'view-hint', hint));
    container.appendChild(status);

    const impact = section('Potential impact and how it could be exploited');
    impact.appendChild(bulletList(f.impact));
    impact.appendChild(el('div', 'view-hint', 'Derived from the CVSS vector; whether your application is exposed depends on how the component is used.'));
    container.appendChild(impact);

    const rem = section('Recommended remediation');
    const plain = f.remediation.filter(r => !r.startsWith('Command'));
    rem.appendChild(bulletList(plain));
    f.remediation.filter(r => r.startsWith('Command')).forEach(r => rem.appendChild(codeLine(r.replace(/^Command \(run it yourself\):\s*/, ''))));
    rem.appendChild(el('div', 'view-hint', 'Shown for you to run - this tool never changes your dependencies.'));
    container.appendChild(rem);

    if (f.details) {
        const d = section('Advisory description');
        const pre = el('pre', 'vd-pre'); pre.textContent = f.details;
        d.appendChild(pre);
        container.appendChild(d);
    }

    const refs = section('References');
    const ul = el('ul', 'vd-list');
    const dbLi = el('li'); dbLi.appendChild(link(f.databaseUrl, 'OSV.dev record for ' + f.databaseId)); ul.appendChild(dbLi);
    f.references.slice(0, 8).forEach(r => { const li = el('li'); li.appendChild(link(r.url, r.url)); ul.appendChild(li); });
    refs.appendChild(ul);
    container.appendChild(refs);
}

function link(url, text) {
    if (!/^https?:\/\//i.test(url)) return el('span', null, text);
    const a = el('a', null, text);
    a.href = url; a.target = '_blank'; a.rel = 'noopener noreferrer';
    return a;
}

function renderVulnExtras(container) {
    const r = vulnReport;
    const det = el('details', 'vuln-extra');
    det.appendChild(el('summary', null, `Dependencies (${r.components.length}) - level, version source and what was or was not checked`));
    const table = el('table', 'vuln-table small');
    const head = el('tr'); ['Level', 'Package', 'Version', 'Version from', 'Status', 'Findings'].forEach(h => head.appendChild(el('th', null, h)));
    const thead = el('thead'); thead.appendChild(head); table.appendChild(thead);
    const tbody = el('tbody');
    r.components.forEach(c => {
        const tr = el('tr');
        const lv = el('td', null, c.level == null ? '-' : 'L' + c.level);
        if (c.via) lv.title = c.via + (c.alsoRequiredBy ? ' (also required by ' + c.alsoRequiredBy + ' other package' + (c.alsoRequiredBy === 1 ? '' : 's') + ')' : '');
        tr.appendChild(lv);
        tr.appendChild(el('td', null, c.name)); tr.appendChild(el('td', null, c.version || '-'));
        tr.appendChild(el('td', null, versionSourceText(c)));
        const st = el('td', null, c.status || (c.result === 'ok' ? 'checked' : c.result));
        st.className = 'dep-status ' + (c.findings > 0 ? 'bad' : (c.resolution === 'unresolved' || c.resolution === 'lookup-unavailable' || c.result === 'failed') ? 'warn' : 'ok');
        tr.appendChild(st); tr.appendChild(el('td', null, String(c.findings)));
        tbody.appendChild(tr);
    });
    table.appendChild(tbody); det.appendChild(table); container.appendChild(det);

    if (r.referencedAssemblies && r.referencedAssemblies.length) {
        const d2 = el('details', 'vuln-extra');
        d2.appendChild(el('summary', null, `Level 1: assemblies referenced by ${r.scannedFor || 'the loaded assembly'} (${r.referencedAssemblies.length}) - as recorded in the assembly`));
        d2.appendChild(el('div', 'view-hint', 'A reference records only an assembly version (e.g. 12.0.0.0). The package version is estimated from it unless a .deps.json is supplied - see "Version from" above.'));
        const t2 = el('table', 'vuln-table small');
        const h2 = el('tr'); ['Assembly', 'Assembly version'].forEach(h => h2.appendChild(el('th', null, h)));
        const th2 = el('thead'); th2.appendChild(h2); t2.appendChild(th2);
        const b2 = el('tbody');
        r.referencedAssemblies.forEach(a => { const tr = el('tr'); tr.appendChild(el('td', null, a.name)); tr.appendChild(el('td', null, a.version)); b2.appendChild(tr); });
        t2.appendChild(b2); d2.appendChild(t2); container.appendChild(d2);
    }
}

// ---------- wiring ----------

byId('btnVulnScan').onclick = runVulnScan;
byId('btnVulnExport').onclick = exportVulnReport;
byId('btnVulnPdf').onclick = exportVulnPdf;
byId('btnVulnDeps').onclick = () => byId('vulnDepsFile').click();
byId('vulnDepsFile').addEventListener('change', (e) => {
    const f = e.target.files[0];
    if (f && hasAssembly) { vulnDepsFile = f; vulnDepsFiles.set(currentAssemblyName, f); syncVulnToolbar(); }
    e.target.value = '';
});
byId('btnVulnDepsClear').onclick = () => { vulnDepsFile = null; if (hasAssembly) vulnDepsFiles.delete(currentAssemblyName); syncVulnToolbar(); };

loadVulnInfo().then(() => { if (state.view === 'vulns') renderVulns(); });
syncVulnToolbar();
