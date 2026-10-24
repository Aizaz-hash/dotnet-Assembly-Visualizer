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
        'and only reads the answer. Nothing is installed or changed.\n\nContinue?');
    if (ok) { try { localStorage.setItem(VULN_CONSENT_KEY, 'yes'); } catch { /* ignore */ } }
    return ok;
}

async function runVulnScan() {
    if (vulnBusy) return;
    const includeApp = byId('vulnOptApp').checked;
    if (!includeApp && !vulnDepsFile) { showMessage('Select at least one source: the application and/or a .deps.json file.', 'error'); return; }
    if (!vulnConsent()) return;

    vulnBusy = true;
    byId('btnVulnScan').disabled = true;
    byId('btnVulnScan').textContent = 'Scanning…';
    setStatus('Scanning dependencies against OSV.dev…');

    const form = new FormData();
    form.append('includeApplication', includeApp ? 'true' : 'false');
    if (vulnDepsFile) form.append('depsFile', vulnDepsFile);
    if (hasAssembly && rawAssemblyData.references && rawAssemblyData.references.length)
        form.append('references', JSON.stringify(rawAssemblyData.references));

    try {
        const response = await fetch('/api/vulnerabilities/scan', { method: 'POST', body: form });
        if (!response.ok) throw new Error((await response.text()) || ('Server returned ' + response.status));
        vulnReport = await response.json();
        vulnReport.scannedFor = hasAssembly ? currentAssemblyName : '';
        applyStoredStatuses(vulnReport);
        vulnSelectedKey = vulnReport.findings.length ? vulnKey(vulnReport.findings[0]) : null;
        const s = vulnReport.summary;
        setStatus(`Scan finished: ${s.total} finding${s.total === 1 ? '' : 's'} (${s.critical} critical, ${s.high} high)` + (vulnReport.complete ? '' : ' - INCOMPLETE'));
    } catch (err) {
        showMessage('Scan failed: ' + err.message, 'error');
        setStatus('Ready');
    } finally {
        vulnBusy = false;
        byId('btnVulnScan').disabled = false;
        byId('btnVulnScan').textContent = vulnReport ? 'Re-run scan' : 'Run scan';
        updateVulnBadge();
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

// ---------- rendering ----------

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
    byId('btnVulnExport').disabled = !vulnReport;

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

function renderVulnIntro(container) {
    const box = el('div', 'vuln-intro');
    box.appendChild(el('h3', null, 'Vulnerability testing'));
    const enabled = !vulnInfo || vulnInfo.enabled !== false;
    box.appendChild(el('p', null, enabled
        ? 'Checks the exact package versions this application ships with - and, if you add its .deps.json, the dependencies of the application you analysed - against the OSV.dev database (GitHub Security Advisories, NVD and others). Every finding shows its CVE, CVSS score, affected component, impact and a recommended fix.'
        : 'Vulnerability scanning is switched off on this server (Vulnerability:Enabled = false).'));
    const ul = el('ul');
    [
        'Only package names and versions are sent to ' + ((vulnInfo && vulnInfo.host) || 'api.osv.dev') + ' - never your code or assemblies.',
        'The scan is read-only: nothing is installed, upgraded or modified. Fix commands are shown for you to run yourself.',
        'Findings are verified against each advisory\'s affected-version ranges and marked confirmed or potential.',
        'If the database cannot be reached, the report says so instead of showing "no vulnerabilities".',
        'Assembly references alone carry only an assembly version (e.g. 8.0.0.0), so for your own application supply its .deps.json for an exact scan.'
    ].forEach(t => ul.appendChild(el('li', null, t)));
    box.appendChild(ul);
    box.appendChild(el('p', 'view-hint', 'Press "Run scan" to start.'));
    container.appendChild(box);
}

function renderVulnBanner(container) {
    const r = vulnReport;
    const ok = r.complete && r.databaseReachable;
    const box = el('div', 'vuln-banner ' + (ok ? 'ok' : (r.databaseReachable ? 'warn' : 'bad')));
    const when = new Date(r.scannedAtUtc).toLocaleString();
    box.appendChild(el('div', 'vb-title', (ok ? '✓ Scan complete' : (r.databaseReachable ? '⚠ Scan incomplete' : '✕ Database unreachable')) +
        ` - ${r.components.length} component${r.components.length === 1 ? '' : 's'} checked against ${r.database} · ${when}`));
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
    ['Severity', 'ID', 'Component', 'Fix', 'Status', 'Confidence'].forEach(h => head.appendChild(el('th', null, h)));
    const thead = el('thead'); thead.appendChild(head); table.appendChild(thead);

    const tbody = el('tbody');
    items.forEach(f => {
        const tr = el('tr', 'vuln-row' + (vulnKey(f) === vulnSelectedKey ? ' selected' : ''));
        const c1 = el('td'); c1.appendChild(sevBadge(f.severity, f.cvssScore)); tr.appendChild(c1);

        const c2 = el('td'); c2.appendChild(el('div', 'vr-id', f.id));
        if (f.databaseId !== f.id) c2.appendChild(el('div', 'vr-sub', f.databaseId));
        c2.appendChild(el('div', 'vr-sum', f.summary)); tr.appendChild(c2);

        const c3 = el('td'); c3.appendChild(el('div', 'vr-id', f.component)); c3.appendChild(el('div', 'vr-sub', f.installedVersion)); tr.appendChild(c3);
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
    fact('Found in', f.componentSource === 'application' ? 'this application' : f.componentSource === 'bundled-script' ? 'bundled script' : 'supplied .deps.json');
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
    det.appendChild(el('summary', null, `Scanned components (${r.components.length}) - what was and was not checked`));
    const table = el('table', 'vuln-table small');
    const head = el('tr'); ['Package', 'Version', 'Source', 'Ships code', 'Result', 'Findings'].forEach(h => head.appendChild(el('th', null, h)));
    const thead = el('thead'); thead.appendChild(head); table.appendChild(thead);
    const tbody = el('tbody');
    r.components.forEach(c => {
        const tr = el('tr');
        tr.appendChild(el('td', null, c.name)); tr.appendChild(el('td', null, c.version));
        tr.appendChild(el('td', null, c.source === 'application' ? 'application' : c.source === 'bundled-script' ? 'bundled script' : 'deps.json'));
        tr.appendChild(el('td', null, c.shipsAssembly === true ? 'yes' : c.shipsAssembly === false ? 'no (framework)' : 'unknown'));
        tr.appendChild(el('td', null, c.result === 'ok' ? 'checked' : c.result)); tr.appendChild(el('td', null, String(c.findings)));
        tbody.appendChild(tr);
    });
    table.appendChild(tbody); det.appendChild(table); container.appendChild(det);

    if (r.referencedAssemblies && r.referencedAssemblies.length) {
        const d2 = el('details', 'vuln-extra');
        d2.appendChild(el('summary', null, `Assemblies referenced by ${r.scannedFor || 'the loaded assembly'} (${r.referencedAssemblies.length}) - listed, not matched to packages`));
        d2.appendChild(el('div', 'view-hint', 'References only record an assembly version (e.g. 8.0.0.0), which does not identify the NuGet package version, so they cannot be checked reliably. Add the application\'s .deps.json to scan its exact packages.'));
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
byId('btnVulnDeps').onclick = () => byId('vulnDepsFile').click();
byId('vulnDepsFile').addEventListener('change', (e) => {
    const f = e.target.files[0];
    if (f) { vulnDepsFile = f; byId('vulnDepsName').textContent = f.name; byId('btnVulnDepsClear').style.display = 'inline-block'; }
    e.target.value = '';
});
byId('btnVulnDepsClear').onclick = () => { vulnDepsFile = null; byId('vulnDepsName').textContent = ''; byId('btnVulnDepsClear').style.display = 'none'; };

loadVulnInfo().then(() => { if (state.view === 'vulns') renderVulns(); });
