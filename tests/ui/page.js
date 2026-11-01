// Checks that run INSIDE the page of the running app (injected by ui.test.mjs through the DevTools protocol).
// They read what the screen really shows and click the real controls; the node side decides pass or fail from what they return.
(() => {
    'use strict';
    const w = (ms) => new Promise(r => setTimeout(r, ms));
    const errors = [];
    // "ResizeObserver loop completed with undelivered notifications" is a harmless browser notice (a canvas resized while the window is being resized), not an exception
    window.addEventListener('error', e => { if (/ResizeObserver loop/.test(e.message)) return; errors.push('error: ' + e.message + ' @' + (e.filename || '').split('/').pop() + ':' + e.lineno); });
    window.addEventListener('unhandledrejection', e => errors.push('rejection: ' + ((e.reason && e.reason.message) || e.reason)));

    /** Polls until fn() is truthy (or the time is up) and returns that value. */
    async function until(fn, ms = 8000, step = 40) {
        const t0 = performance.now();
        while (performance.now() - t0 < ms) { try { const v = fn(); if (v) return v; } catch { /* not ready yet */ } await w(step); }
        return null;
    }

    const byId = (id) => document.getElementById(id);
    const activeSection = () => [...document.querySelectorAll('.view.active')].map(x => x.id.replace('view', '')).join(',');
    const row = (title) => [...document.querySelectorAll('#explorerTree .t-row')].find(r => r.title === title || r.title.startsWith(title + '\n'));
    const visibleCodeLabel = () => { const l = [...document.querySelectorAll('#inhCodeScroll .src-label, #callCodeScroll .src-label')].find(x => x.offsetParent !== null); return l ? l.textContent : null; };
    const visibleCode = () => { const p = [...document.querySelectorAll('#inhCodeScroll .src-code, #callCodeScroll .src-code')].find(x => x.offsetParent !== null); return p && !/^Decompiling/.test(p.textContent) ? p.textContent : null; };

    function snap() {
        return {
            view: state.view, ovTab: state.ovTab, section: activeSection(), type: state.type, method: state.method, cycleAll: state.cycleAll,
            title: (byId('viewTitle').textContent || ''), bar: byId('contextBar').innerText.replace(/\s+/g, ' '), code: visibleCodeLabel(),
            toasts: [...document.querySelectorAll('#toastHost .toast')].map(t => t.textContent.trim())
        };
    }

    async function waitAssembly(name) {
        await until(() => hasAssembly && currentAssemblyName === name && getComputedStyle(byId('appLoader')).display === 'none', 20000);
        await w(150);
        document.querySelectorAll('#toastHost .toast').forEach(t => t.remove());
        return currentAssemblyName === name;
    }

    async function expandAll() {
        for (let pass = 0; pass < 4; pass++) for (const r of [...document.querySelectorAll('#explorerTree .t-row')]) { const a = r.querySelector('.t-arrow'); if (a && a.textContent === '▸') { a.click(); await w(15); } }
    }

    // ---------------------------------------------------------------- class and method sweeps (real clicks on the explorer)

    async function classSweep() {
        await expandAll(); const bad = []; let n = 0;
        for (const id of [...typeNodes.keys()]) {
            const r = row(id); if (!r) { bad.push(id + ': no explorer row'); continue; }
            r.click(); n++;
            const t = typeNodes.get(id), p = [];
            await until(() => state.type === id && state.view === 'navigator' && inhNetwork && inhNetwork.body.data.nodes.get(id));
            await until(() => visibleCodeLabel() === id && visibleCode(), 20000);
            const s = snap();
            if (s.view !== 'navigator' || s.section !== 'Inheritance') p.push('view=' + s.view + '/' + s.section);
            if (s.type !== id) p.push('state.type=' + s.type);
            if (s.method !== null) p.push('method not cleared');
            if (!s.title.includes(t.name)) p.push('title=' + s.title);
            if (!s.bar.includes(t.name)) p.push('selection bar=' + s.bar);
            const node = inhNetwork && inhNetwork.body.data.nodes.get(id);
            if (!node || node.borderWidth !== 3) p.push('selected node missing or not highlighted');
            const code = visibleCode();
            if (s.code !== id) p.push('code label=' + s.code);
            const simple = t.name.split('.').pop().replace(/`\d+$/, '').replace(/<.*$/, '');
            if (!code || !code.includes(simple)) p.push('code lacks ' + simple);
            if (p.length) bad.push(id + ': ' + p.join('; '));
        }
        return { checked: n, bad };
    }

    async function methodSweep() {
        await expandAll(); const bad = []; let n = 0;
        for (const m of [...methodById.values()]) {
            const r = row(m.id); if (!r) { bad.push(m.id + ': no explorer row'); continue; }
            r.click(); n++;
            const p = [], simple = m.name.replace(/\(.*$/, '');
            await until(() => state.method === m.id && callNetwork && callNetwork.body.data.nodes.get(m.id));
            await until(() => visibleCodeLabel() === m.parentClass + '.' + m.name && visibleCode(), 20000);
            const s = snap();
            if (s.view !== 'navigator' || s.section !== 'Calls') p.push('view=' + s.view + '/' + s.section);
            if (s.method !== m.id) p.push('state.method=' + s.method);
            if (s.type !== m.parentClass) p.push('state.type=' + s.type);
            const node = callNetwork && callNetwork.body.data.nodes.get(m.id);
            if (!node || node.borderWidth !== 3) p.push('main node missing or not highlighted');
            if (s.code !== m.parentClass + '.' + m.name) p.push('code label=' + s.code);
            const code = visibleCode();
            // what the code must mention: a constructor -> its class; an accessor -> the property; a lambda body (named "Outer.Name") has a compiler-generated name, so only the label is checked
            const cls = m.parentClass.split('.').pop().replace(/`\d+$/, '');
            let needle = null;
            if (/(^|\.)\.?c?ctor$/.test(simple) || simple.startsWith('.')) needle = cls;
            else if (!simple.includes('.')) needle = simple.replace(/^(get_|set_|add_|remove_)/, '');
            if (needle && (!code || !code.includes(needle))) p.push('code lacks ' + needle);
            if (!code) p.push('no code shown');
            if (p.length) bad.push(m.id + ': ' + p.join('; '));
        }
        return { checked: n, bad };
    }

    // ---------------------------------------------------------------- circular dependencies

    async function cycleChecks() {
        await expandAll(); const bad = []; const stats = () => byId('cycleStats').textContent;
        // 1. every consecutive pair of every chain is a real call, the chain closes, and no method repeats
        cycles.forEach((c, i) => { const b = c.body; for (let k = 0; k < b.length; k++) if (!(calleesOf.get(b[k]) || []).includes(b[(k + 1) % b.length])) bad.push('chain ' + (i + 1) + ': not a real call loop'); if (new Set(b).size !== b.length) bad.push('chain ' + (i + 1) + ' repeats a method'); });
        // 2. classes
        for (const id of [...typeNodes.keys()]) {
            const own = (methodsByClass.get(id) || []).map(m => m.id);
            const truth = cycles.filter(c => c.body.some(m => own.includes(m))).length;
            row(id).click(); await until(() => state.type === id && state.method === null);
            const badge = document.querySelector('#contextBar .cb-cycle');
            if (!!badge !== (truth > 0)) bad.push(id + ': class badge ' + (badge ? 'shown' : 'missing') + ' but it is in ' + truth + ' chains');
            if (badge) {
                badge.click(); await until(() => state.view === 'cycles');
                const s = snap(); const m = stats().match(/^(\d+) of/);
                if (s.view !== 'cycles' || s.type !== id || s.method !== null) bad.push(id + ': badge opened view=' + s.view + ' type=' + s.type);
                if (!m || +m[1] !== truth) bad.push(id + ': Cycles shows ' + (m && m[1]) + ' chains, expected ' + truth);
                if (document.querySelectorAll('#cycleList .cyc-item').length !== truth) bad.push(id + ': chain rows differ from ' + truth);
                byId('btnBack').click(); await until(() => state.view === 'navigator');
                const b = snap(); if (b.view !== 'navigator' || b.type !== id) bad.push(id + ': Back from Cycles lands on ' + b.view + '/' + b.type);
            }
        }
        // 3. methods
        for (const m of methodById.values()) {
            const inCycle = cyclesOfMethod.has(m.id);
            row(m.id).click(); await until(() => state.method === m.id);
            const badge = document.querySelector('#contextBar .cb-cycle'); const note = byId('methodNote');
            const noteShown = !!note && note.style.display !== 'none' && note.textContent.trim().length > 0;
            if (!!badge !== inCycle) bad.push(m.name.slice(0, 30) + ': badge ' + !!badge + ' vs in cycle ' + inCycle);
            if (noteShown !== inCycle) bad.push(m.name.slice(0, 30) + ': method note ' + noteShown + ' vs in cycle ' + inCycle);
            if (/⚠/.test(row(m.id).textContent) !== inCycle) bad.push(m.name.slice(0, 30) + ': explorer marker differs');
            if (inCycle) {
                badge.click(); await until(() => state.view === 'cycles');
                const s = snap(), k = stats().match(/^(\d+) of/);
                if (s.view !== 'cycles' || s.method !== m.id || s.type !== m.parentClass) bad.push(m.name.slice(0, 30) + ': badge lost the selection');
                if (!k || +k[1] !== cyclesOfMethod.get(m.id).length) bad.push(m.name.slice(0, 30) + ': shows ' + (k && k[1]) + ' chains, expected ' + cyclesOfMethod.get(m.id).length);
                byId('cbOpenNavigator').click(); await until(() => state.view === 'navigator');
                if (snap().method !== m.id) bad.push(m.name.slice(0, 30) + ': back to the Navigator lost the method');
            }
        }
        return { chains: cycles.length, bad };
    }

    // ---------------------------------------------------------------- navigation sequences (real clicks)

    async function sequences() {
        const log = [], bad = []; const chk = (label, cond, extra) => { log.push(label); if (!cond) bad.push(label + (extra ? ' -> ' + extra : '')); };
        const inhSequences = async () => {
            await expandAll(); const RECT = 'Samples.Inheritance.Rectangle', SHAPE = 'Samples.Inheritance.Shape';
            row(RECT).click(); await until(() => state.type === RECT); let s = snap();
            chk('S1 a class opens the Navigator on its inheritance', s.view === 'navigator' && s.section === 'Inheritance' && s.type === RECT);
            const members = [...document.querySelectorAll('#inhMembersList .t-row')];
            chk('S1 the members panel lists exactly the class methods', members.length === (methodsByClass.get(RECT) || []).length);
            const area = methodsByClass.get(RECT).find(m => /^Area/.test(m.name));
            members.find(r => r.title === area.id).click(); await until(() => state.method === area.id); await until(() => visibleCodeLabel() === RECT + '.' + area.name);
            chk('S1 a method from the members panel opens its calls and its code', snap().section === 'Calls' && snap().method === area.id && visibleCodeLabel() === RECT + '.' + area.name);
            byId('btnBack').click(); await until(() => state.method === null && visibleCodeLabel() === RECT); s = snap();
            chk('S1 Back returns to the class with its code', s.section === 'Inheritance' && s.type === RECT && s.method === null && s.code === RECT, JSON.stringify([s.section, s.type, s.method, s.code]));
            row(SHAPE).click(); await until(() => state.type === SHAPE && inhNetwork);
            const derived = (derivedOf.get(SHAPE) || []).map(d => d.id).find(id => typeNodes.has(id));
            chk('S2 Shape has a derived class', !!derived);
            inhNetwork.emit('doubleClick', { nodes: [derived] }); await until(() => state.type === derived && visibleCodeLabel() === derived);
            chk('S2 double-clicking the derived class selects it and shows its code', state.type === derived && visibleCodeLabel() === derived);
            const mm = methodsByClass.get(derived)[0];
            [...document.querySelectorAll('#inhMembersList .t-row')].find(r => r.title === mm.id).click(); await until(() => state.method === mm.id && visibleCodeLabel() === derived + '.' + mm.name);
            chk('S2 then a method of the derived class', state.method === mm.id && state.type === derived && visibleCodeLabel() === derived + '.' + mm.name);
        };
        const cycSequences = async () => {
            await expandAll(); const LEFT = 'Samples.Cycles.Left', MIDDLE = 'Samples.Cycles.Middle';
            row(LEFT).click(); await until(() => state.type === LEFT);
            document.querySelector('#contextBar .cb-cycle').click(); await until(() => state.view === 'cycles');
            chk('S3 the class warning opens Cycles for that class', state.view === 'cycles' && state.type === LEFT);
            const token = [...document.querySelectorAll('#cycleChain .chain-method')].find(t => t.title.startsWith(MIDDLE + '.'));
            chk('S3 the chain lists a method of the related class', !!token);
            token.click(); await until(() => state.type === MIDDLE && visibleCodeLabel());
            chk('S3 clicking it opens that method with its code', state.view === 'navigator' && /^Samples\.Cycles\.Middle\.Step/.test(visibleCodeLabel() || ''), visibleCodeLabel());
            byId('btnBack').click(); await until(() => state.view === 'cycles');
            chk('S3 Back returns to the cycles of the first class', state.view === 'cycles' && state.type === LEFT);
            row(LEFT).click(); await until(() => state.type === LEFT && state.method === null);
            const step = methodsByClass.get(LEFT)[0];
            [...document.querySelectorAll('#inhMembersList .t-row')].find(r => r.title === step.id).click(); await until(() => state.method === step.id);
            document.querySelector('#contextBar .cb-cycle').click(); await until(() => state.view === 'cycles');
            chk('S4 the method warning opens Cycles for that method', state.view === 'cycles' && state.method === step.id && state.type === LEFT);
            byId('cbOpenNavigator').click(); await until(() => state.view === 'navigator');
            chk('S4 back in the Navigator the method is still selected', state.method === step.id);
            const crumb = document.querySelector('#viewTitle .crumb'); chk('S4 the "Inheritance" link is there', !!crumb && /Inheritance/.test(crumb.textContent));
            crumb.click(); await until(() => state.method === null && visibleCodeLabel() === LEFT);
            chk('S4 it shows the same class: inheritance and its code', state.type === LEFT && state.method === null && snap().section === 'Inheritance' && visibleCodeLabel() === LEFT);
            let back = 0; while (!byId('btnBack').disabled && back < 100) { byId('btnBack').click(); await w(20); back++; }
            let fwd = 0; while (!byId('btnForward').disabled && fwd < 100) { byId('btnForward').click(); await w(20); fwd++; }
            await until(() => state.type === LEFT && state.method === null);
            chk('Back to the first screen and Forward to the last returns to the same place', state.type === LEFT && snap().section === 'Inheritance', JSON.stringify([back, fwd, state.type]));
        };
        return { inhSequences, cycSequences, result: () => ({ checks: log.length, bad }) };
    }

    // ---------------------------------------------------------------- search

    async function searchChecks() {
        await expandAll(); const log = [], bad = []; const chk = (label, cond, extra) => { log.push(label); if (!cond) bad.push(label + (extra ? ' -> ' + extra : '')); };
        const box = byId('searchBox'), tree = () => byId('explorerTree');
        const search = async (q) => { box.value = q; box.dispatchEvent(new Event('input', { bubbles: true })); await w(500); return [...tree().querySelectorAll('.t-row')].map(r => r.title.split('\n')[0]); };
        const RECT = 'Samples.Inheritance.Rectangle'; row(RECT).click(); await until(() => state.type === RECT && visibleCodeLabel() === RECT);
        const before = snap();
        let r = await search('rect'); chk('partial "rect" finds Rectangle', r.includes(RECT));
        r = await search('Rectangle'); chk('exact class name', r.includes(RECT));
        r = await search('RECTANGLE'); chk('upper case finds it too', r.includes(RECT));
        r = await search('Area'); chk('a method name finds its methods', r.some(t => /\.Area\(/.test(t)));
        r = await search('Samples.Inheritance'); chk('a namespace lists its types', r.length >= 10, r.length + ' rows');
        r = await search('R'); chk('one character does not filter', r.length >= 10, r.length + ' rows');
        const none = await search('zzzzqq'); chk('no result: no type rows and a message', !none.some(t => /^Samples/.test(t)) && tree().innerText.trim().length > 0, tree().innerText.slice(0, 50));
        const s1 = snap(); chk('the selection is unchanged after all those searches', s1.type === before.type && s1.method === before.method && s1.view === before.view && s1.code === before.code);
        await search('circle'); const circle = [...tree().querySelectorAll('.t-row')].find(x => x.title.split('\n')[0] === 'Samples.Inheritance.Circle'); chk('a result for Circle is listed', !!circle);
        circle.click(); await until(() => state.type === 'Samples.Inheritance.Circle' && visibleCodeLabel() === 'Samples.Inheritance.Circle');
        chk('clicking a result selects exactly that class and shows its code', state.type === 'Samples.Inheritance.Circle' && visibleCodeLabel() === 'Samples.Inheritance.Circle');
        await search(''); const sel = [...tree().querySelectorAll('.t-row')].find(x => x.className.includes('selected'));
        chk('clearing restores the tree with the selection highlighted', !!sel && sel.title.split('\n')[0] === 'Samples.Inheritance.Circle');
        const errorsBefore = errors.length;
        for (const q of ['(', '[a', '.*', '\\', '<>', "a'b", '`1']) await search(q);
        chk('special characters never throw', errors.length === errorsBefore, errors.slice(errorsBefore).join(' | '));
        await search('');
        return { checks: log.length, bad };
    }

    // ---------------------------------------------------------------- readability (contrast of every visible text)

    function contrastAudit(min = 4.4) {
        const parse = (c) => { const m = c.match(/rgba?\(([^)]+)\)/); if (!m) return null; const p = m[1].split(',').map(parseFloat); return { r: p[0], g: p[1], b: p[2], a: p.length > 3 ? p[3] : 1 }; };
        const lum = ({ r, g, b }) => { const f = (v) => { v /= 255; return v <= .03928 ? v / 12.92 : Math.pow((v + .055) / 1.055, 2.4); }; return .2126 * f(r) + .7152 * f(g) + .0722 * f(b); };
        const bgOf = (el) => { let e = el, layers = []; while (e) { const c = parse(getComputedStyle(e).backgroundColor); if (c && c.a > 0) { layers.push(c); if (c.a >= 1) break; } e = e.parentElement; } let base = { r: 255, g: 255, b: 255 }; for (let i = layers.length - 1; i >= 0; i--) { const l = layers[i]; base = { r: l.r * l.a + base.r * (1 - l.a), g: l.g * l.a + base.g * (1 - l.a), b: l.b * l.a + base.b * (1 - l.a) }; } return base; };
        const bad = [], seen = new Set(); const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT); let n;
        while ((n = walker.nextNode())) {
            const t = n.textContent.trim(); if (t.length < 2) continue; const el = n.parentElement; if (!el || seen.has(el)) continue; seen.add(el);
            const cs = getComputedStyle(el); if (cs.visibility === 'hidden' || cs.display === 'none') continue;
            const r = el.getBoundingClientRect(); if (r.width < 2 || r.height < 2) continue;
            if (el.closest('[style*="display: none"], .view:not(.active)') || el.closest('.tab.disabled') || el.disabled) continue;
            const fg = parse(cs.color); if (!fg) continue; const bg = bgOf(el);
            const L1 = lum({ r: fg.r * fg.a + bg.r * (1 - fg.a), g: fg.g * fg.a + bg.g * (1 - fg.a), b: fg.b * fg.a + bg.b * (1 - fg.a) }), L2 = lum(bg);
            const ratio = (Math.max(L1, L2) + .05) / (Math.min(L1, L2) + .05);
            if (ratio < min) bad.push(el.tagName.toLowerCase() + '.' + (el.className || '').toString().split(' ')[0] + ' "' + t.slice(0, 24) + '" ' + ratio.toFixed(2));
        }
        return bad;
    }

    async function goTo(name, sampleCls) {
        const step = methodsByClass.get(sampleCls)[0].id;
        ({
            'overview': () => navigate({ view: 'overview', ovTab: 'summary' }), 'architecture': () => navigate({ view: 'overview', ovTab: 'architecture' }), 'metrics': () => navigate({ view: 'overview', ovTab: 'metrics' }),
            'navigator-class': () => selectClass(sampleCls), 'navigator-method': () => selectMethod(step),
            'cycles': () => navigate({ view: 'cycles', type: null, method: null, cycleAll: true }), 'risky': () => navigate({ view: 'risky' }), 'vulns': () => navigate({ view: 'vulns' }), 'compare': () => navigate({ view: 'compare' })
        })[name]();
        await w(450); if (/navigator/.test(name)) await until(() => visibleCode(), 6000);
    }
    const SCREENS = ['overview', 'architecture', 'metrics', 'navigator-class', 'navigator-method', 'cycles', 'risky', 'vulns', 'compare'];

    async function themeAudit(theme, sampleCls) {
        Theme.set(theme); await w(300); const res = {};
        for (const s of SCREENS) { await goTo(s, sampleCls); const a = contrastAudit(); if (a.length) res[s] = a; }
        return res;
    }

    async function themeSwitchKeepsState(sampleCls) {
        const bad = []; const step = methodsByClass.get(sampleCls)[0];
        await expandAll();
        row(step.id).click(); await until(() => state.method === step.id && visibleCodeLabel() === step.parentClass + '.' + step.name);
        const before = snap();
        for (const theme of ['dark', 'light', 'system', 'dark', 'light']) {
            Theme.set(theme); await w(500); await until(() => visibleCode());
            const s = snap(), resolved = Theme.resolve(theme, matchMedia('(prefers-color-scheme: dark)').matches);
            if (!(s.view === before.view && s.type === before.type && s.method === before.method && s.code === before.code && s.section === before.section)) bad.push(theme + ': the selection or the code changed');
            if (document.documentElement.dataset.theme !== resolved) bad.push(theme + ': page theme is ' + document.documentElement.dataset.theme + ', expected ' + resolved);
            if (byId('themeSelect').value !== theme) bad.push(theme + ': the select shows ' + byId('themeSelect').value);
            const g = callNetwork && callNetwork.body.data.nodes.get(step.id);
            if (!g || g.color.background !== Theme.colors().bad.background) bad.push(theme + ': the graph was not redrawn with this theme (' + (g && g.color.background) + ')');
        }
        return bad;
    }

    // ---------------------------------------------------------------- layout

    function overflow() {
        const pane = byId('mainPane').getBoundingClientRect(); const out = new Map();
        const scrollable = (el) => { for (let e = el.parentElement; e && e !== document.body; e = e.parentElement) { if (/(auto|scroll|hidden)/.test(getComputedStyle(e).overflowX)) return e; } return null; };
        for (const el of document.querySelectorAll('#mainPane *')) {
            const r = el.getBoundingClientRect(); if (r.width < 2 || r.height < 2) continue; const cs = getComputedStyle(el); if (cs.display === 'none' || cs.visibility === 'hidden') continue;
            if (el.closest('.view:not(.active)') || el.closest('[style*="display: none"]')) continue;
            if (r.right > pane.right + 2 && !scrollable(el)) { const key = el.tagName.toLowerCase() + '.' + (el.className || '').toString().split(' ')[0]; if (!out.has(key)) out.set(key, Math.round(r.right - pane.right)); }
        }
        const de = document.documentElement; return { page: de.scrollWidth > de.clientWidth + 1, items: [...out.entries()].slice(0, 6).map(([k, v]) => k + ' +' + v) };
    }
    function sideScroll() {
        const out = []; for (const el of document.querySelectorAll('#mainPane *, #explorer *')) { if (el.closest('.view:not(.active)')) continue; if (!/(auto|scroll)/.test(getComputedStyle(el).overflowX)) continue; if (el.scrollWidth > el.clientWidth + 2 && el.clientWidth > 0) out.push((el.id || el.className.toString().split(' ')[0] || el.tagName)); }
        return out;
    }
    async function layout(sampleCls) {
        const res = {};
        for (const s of SCREENS) { await goTo(s, sampleCls); const o = overflow(), sd = sideScroll(); if (o.page || o.items.length || sd.length) res[s] = { page: o.page, overflow: o.items, sideways: sd }; }
        return res;
    }

    // ---------------------------------------------------------------- big assembly

    async function bigAssembly() {
        const out = {}; const time = async (label, fn) => { const t = performance.now(); await fn(); out[label] = Math.round(performance.now() - t); };
        out.types = typeNodes.size; out.methods = methodById.size; out.cycles = cycles.length;
        const biggest = [...methodsByClass.entries()].sort((a, b) => b[1].length - a[1].length)[0];
        await time('biggestClassMs', async () => { navigate({ view: 'navigator' }); selectClass(biggest[0]); await until(() => visibleCodeLabel() === biggest[0] && visibleCode(), 60000); });
        out.biggestClass = biggest[0].split('.').pop(); out.biggestMethods = biggest[1].length;
        const code = visibleCode(); out.codeLength = code ? code.length : 0; out.highlighted = code ? document.querySelector('#inhCodeScroll .src-code').querySelectorAll('[class^=cs-]').length : 0;
        const sc = byId('inhCodeScroll'); sc.scrollTop = 4000; await w(100); out.codeScrolls = sc.scrollTop > 0;
        await time('wholeGraphMs', async () => { navigate({ inhMode: 'all' }); await until(() => inhNetwork && inhNetwork.body.data.nodes.length > 10, 15000); });
        out.wholeGraphNodes = inhNetwork ? inhNetwork.body.data.nodes.length : 0; navigate({ inhMode: 'selected' });
        await time('architectureMs', async () => { navigate({ view: 'overview', ovTab: 'architecture' }); await until(() => document.querySelector('#archCanvas canvas'), 15000); });
        await time('metricsMs', async () => { navigate({ view: 'overview', ovTab: 'metrics' }); await w(300); });
        await time('cyclesMs', async () => { navigate({ view: 'cycles', type: null, method: null, cycleAll: true }); await until(() => document.querySelectorAll('#cycleList .cyc-item').length === cycles.length, 15000); });
        out.cycleRows = document.querySelectorAll('#cycleList .cyc-item').length;
        const box = byId('searchBox'); box.value = 'Json'; box.dispatchEvent(new Event('input', { bubbles: true })); await w(900);
        out.searchRows = document.querySelectorAll('#explorerTree .t-row').length; out.searchHeader = byId('explorerTree').firstElementChild.textContent.trim();
        box.value = ''; box.dispatchEvent(new Event('input', { bubbles: true })); await w(300);
        return out;
    }

    // ---------------------------------------------------------------- bad inputs

    async function badInput(name, fileSelectorDone) {
        // the node side has put the file in the input; wait for the answer
        const before = { asm: currentAssemblyName, types: typeNodes.size };
        const toast = await until(() => { const t = [...document.querySelectorAll('#toastHost .toast')]; return t.length ? t.map(x => x.textContent.trim()).join(' || ') : null; }, 15000);
        await w(300);
        const loaderShown = getComputedStyle(byId('appLoader')).display !== 'none';
        document.querySelectorAll('#toastHost .toast').forEach(t => t.remove());
        return { toast: toast || '', keptAssembly: currentAssemblyName === before.asm && typeNodes.size === before.types, loaderStuck: loaderShown };
    }

    window.UI = { errors, until, w, snap, waitAssembly, expandAll, row, classSweep, methodSweep, cycleChecks, sequences, searchChecks, themeAudit, themeSwitchKeepsState, layout, bigAssembly, badInput, contrastAudit, visibleCode, visibleCodeLabel, goTo };
    return 'ui helpers ready';
})();
