// Risky APIs tab: calls found in the loaded assembly to APIs that deserve a security look
// (unsafe deserializers, process start, dynamic assembly loading, weak crypto, native calls, disabled TLS checks).
// The list comes from the analysis (riskyApis); nothing is sent anywhere.
'use strict';

const riskyFilters = { severity: '', text: '' };

function riskyList() {
    return (hasAssembly && rawAssemblyData.riskyApis) ? rawAssemblyData.riskyApis : [];
}

function riskyOnAssemblyChanged() {
    const n = riskyList().length;
    setTabBadge(byId('riskyBadge'), n, n ? n + ' risky API use' + (n === 1 ? '' : 's') + ' found' : 'No risky API calls found');
    riskyFilters.severity = riskyFilters.text = '';
}

function renderRisky() {
    const root = byId('riskyScroll');
    root.innerHTML = '';
    if (!hasAssembly) { root.appendChild(el('div', 'empty-note', 'Open a .NET assembly to see which risky APIs it calls.')); return; }

    const all = riskyList();
    const box = el('div', 'risky-intro');
    box.appendChild(el('p', null, 'Calls to APIs that deserve a security review: unsafe deserializers, starting processes, loading code at run time, weak cryptography, native calls and disabled certificate checks. These are leads, not vulnerabilities - most uses are fine, so each entry says what to check. Found by reading the code of ' + currentAssemblyName + '; nothing runs and nothing is sent anywhere.'));
    root.appendChild(box);

    if (!all.length) {
        root.appendChild(el('div', 'vuln-banner ok', '✓ No calls to the risky APIs this scan knows about were found in ' + currentAssemblyName + '. That is not proof the code is safe - it only covers the listed APIs.'));
        return;
    }

    const count = (s) => all.filter(r => r.severity === s).length;
    const cards = el('div', 'risky-counts');
    ['High', 'Medium', 'Low'].forEach(s => {
        const card = el('div', 'risky-count sev-' + s.toLowerCase() + (riskyFilters.severity === s ? ' active' : ''));
        card.appendChild(el('div', 'rcn-num', String(count(s))));
        card.appendChild(el('div', 'rcn-label', s));
        card.onclick = () => { riskyFilters.severity = riskyFilters.severity === s ? '' : s; renderRisky(); };
        cards.appendChild(card);
    });
    root.appendChild(cards);

    const search = el('input'); search.type = 'search'; search.placeholder = 'Filter by API, category or method…'; search.value = riskyFilters.text;
    search.oninput = () => { riskyFilters.text = search.value; renderRiskyItems(); };
    const bar = el('div', 'risky-filter'); bar.appendChild(search); root.appendChild(bar);

    const items = el('div', 'risky-items'); items.id = 'riskyItems'; root.appendChild(items);
    renderRiskyItems();
}

function renderRiskyItems() {
    const host = byId('riskyItems');
    if (!host) return;
    host.innerHTML = '';
    const q = riskyFilters.text.trim().toLowerCase();
    const shown = riskyList().filter(r =>
        (!riskyFilters.severity || r.severity === riskyFilters.severity) &&
        (!q || (r.api + ' ' + r.category + ' ' + r.title + ' ' + r.callers.join(' ')).toLowerCase().includes(q)));
    if (!shown.length) { host.appendChild(el('div', 'empty-note', 'Nothing matches the filter.')); return; }

    shown.forEach(r => {
        const card = el('div', 'risky-card');
        const head = el('div', 'rc-head');
        head.appendChild(sevBadge(r.severity, null));
        head.appendChild(el('span', 'rc-title', r.title));
        head.appendChild(el('span', 'rc-cat', r.category));
        card.appendChild(head);
        card.appendChild(el('div', 'rc-api', r.api + ' · ' + r.count + ' call' + (r.count === 1 ? '' : 's')));
        card.appendChild(el('div', 'rc-why', r.why));
        const advice = el('div', 'rc-advice'); advice.appendChild(el('b', null, 'What to check: ')); advice.appendChild(document.createTextNode(r.advice)); card.appendChild(advice);

        const callers = el('div', 'rc-callers');
        callers.appendChild(el('span', 'rc-callers-label', 'Called from' + (r.count > r.callers.length ? ' (first ' + r.callers.length + ' methods)' : '') + ': '));
        r.callers.forEach(id => {
            const info = methodInfo(id);
            const a = el('a', 'rc-caller', info.className.split('.').pop() + '.' + info.name);
            a.href = '#'; a.title = id;
            a.onclick = (e) => { e.preventDefault(); navigate({ view: 'calls', type: info.className, method: id }); };
            callers.appendChild(a);
        });
        card.appendChild(callers);
        host.appendChild(card);
    });
}
