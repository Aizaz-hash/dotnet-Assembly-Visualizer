// One end-to-end test of the whole user interface, in a real (headless) browser.
//
//   node --test tests/ui/*.test.mjs
//
// It starts the app, opens a headless Edge / Chrome through the DevTools protocol (no extra packages), loads the sample assemblies the way a user does
// (the file input, then real clicks on the explorer, tabs and links) and checks what the screen shows: start-up, navigation, class and method context across
// every view, circular dependencies, search, bad files, themes (including the system theme changing live), readability, layout at several window sizes and a
// large real library. The checks that run inside the page are in page.js.
//
// Needs: the app built, the sample assemblies built (they are copied next to the test project: run `dotnet test tests/DotNetVisualizer.Web.Tests` once) and
// a Chromium browser. Without one of them the test is skipped and says why. Settings (all optional):
//   UI_TEST_URL      use an app that is already running instead of starting one
//   UI_BROWSER       path to the browser executable
//   UI_SAMPLES       folder with the sample assemblies
//   UI_TEST_BUILD=1  let `dotnet run` build first (by default the existing build is used)
import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, mkdtempSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { root, browserPath, startApp, startBrowser } from './browser.mjs';

// ---------------------------------------------------------------- what is needed

function findSamples() {
    const candidates = [process.env.UI_SAMPLES, ...['Debug', 'Release'].map(c => join(root, 'tests', 'DotNetVisualizer.Web.Tests', 'bin', c, 'net8.0', 'samples'))].filter(Boolean);
    return candidates.find(d => existsSync(join(d, 'Inheritance.dll'))) || null;
}
const samplesDir = findSamples();
const skipReason = !browserPath ? 'no Chromium browser found (set UI_BROWSER)' : !samplesDir ? 'sample assemblies not built (run: dotnet test tests/DotNetVisualizer.Web.Tests)' : null;

const SAMPLE = (name) => join(samplesDir, name);
const FILES = {
    Inheritance: () => SAMPLE('Inheritance.dll'), MethodCalls: () => SAMPLE('MethodCalls.dll'), Circular: () => SAMPLE('CircularDependencies.dll'), Metrics: () => SAMPLE('MetricsDemo.dll'),
    Risky: () => SAMPLE('RiskyApis.dll'), Obfuscation: () => SAMPLE('ObfuscationLike.dll'), Vulnerable: () => SAMPLE('VulnerableDemo.dll'), ApiCompatV1: () => join(samplesDir, 'api-compat', 'v1', 'ApiCompat.dll')
};
const bigLibrary = () => [join(samplesDir, '..', 'Newtonsoft.Json.dll'), join(root, 'tests', 'DotNetVisualizer.Web.Tests', 'bin', 'Debug', 'net8.0', 'Newtonsoft.Json.dll')].find(existsSync);

// ---------------------------------------------------------------- the test

test('the whole user interface, in a real browser', { skip: skipReason || false, timeout: 15 * 60 * 1000 }, async (t) => {
    const app = await startApp();
    const b = await startBrowser();
    const scratch = mkdtempSync(join(tmpdir(), 'dav-ui-files-'));
    t.after(async () => { await b.close(); app.stop(); try { rmSync(scratch, { recursive: true, force: true }); } catch { } });

    const expectNone = (list, what) => assert.deepEqual(list, [], what + ':\n  ' + [].concat(list).join('\n  '));
    const fresh = async () => { await b.viewport(1400, 850); await b.colorScheme('light'); await b.goto(app.url + '/index.html'); await b.evaluate("try { localStorage.clear(); sessionStorage.clear(); } catch (e) {} 1"); await b.reload(); await b.injectHelpers(); };

    // A trace of the first moments of every page load, one sample per painted frame: the workspace must never be shown before the first screen is drawn.
    await b.send('Page.addScriptToEvaluateOnNewDocument', { source: `window.__trace = []; (function () {
        function sig() { var d = document, ws = d.getElementById('workspace'); return { vis: ws ? getComputedStyle(ws).visibility : null, views: d.querySelectorAll('.view.active').length, ready: !!(d.body && d.body.classList.contains('app-ready')), select: (d.getElementById('themeSelect') || {}).value || null, theme: d.documentElement.dataset.theme || null }; }
        var last = '', frames = 0, after = 0;
        function frame() { var s = sig(), k = JSON.stringify(s); if (k !== last) { last = k; window.__trace.push(s); } frames++; if (s.ready) after++; if (frames < 900 && after < 4) requestAnimationFrame(frame); }
        requestAnimationFrame(frame); })();` });

    await t.test('start-up: the first screen is the introduction, complete, with the right tabs enabled', async () => {
        await fresh();
        const info = await b.evaluate(`(async () => { await UI.until(() => document.body.classList.contains('app-ready')); return {
            snap: UI.snap(), tabs: [...document.querySelectorAll('.tab')].map(x => x.id.replace('tab', '') + (x.classList.contains('disabled') ? ':off' : ':on') + (x.classList.contains('active') ? ':active' : '')),
            intro: document.getElementById('overviewScroll').innerText.slice(0, 60), search: document.getElementById('searchBox').disabled, themeSelect: document.getElementById('themeSelect').value,
            starts: [...document.querySelectorAll('.ov-start-title')].map(x => x.textContent), workspace: getComputedStyle(document.getElementById('workspace')).visibility } })()`);
        assert.equal(info.snap.view, 'overview'); assert.equal(info.snap.ovTab, 'summary');
        assert.deepEqual(info.tabs, ['Overview:on:active', 'Navigator:off', 'Cycles:off', 'Risky:off', 'Vulns:off', 'Compare:on']);
        assert.match(info.intro, /Understand a .NET assembly/); assert.deepEqual(info.starts, ['Open an assembly', 'Compare two versions']);
        assert.equal(info.search, true); assert.equal(info.themeSelect, 'system'); assert.equal(info.workspace, 'visible');
        expectNone(b.pageErrors, 'script errors at start-up');
    });

    await t.test('start-up: nothing half-drawn is ever shown (no flash of an empty page)', async () => {
        await b.evaluate("Theme.set('dark'); 1"); await b.reload(); await b.injectHelpers();
        // the sampler records one entry per painted frame: wait until it has seen the finished page
        assert.equal(await b.evaluate('new Promise(res => { const t = setInterval(() => { if (window.__trace.some(s => s.ready)) { clearInterval(t); res(true); } }, 50); setTimeout(() => { clearInterval(t); res(false); }, 8000); })'), true, 'the page never reported ready');
        const trace = await b.evaluate('window.__trace');
        const early = trace.filter(s => s.vis === 'visible' && s.views === 0);
        assert.deepEqual(early, [], 'the workspace was visible before a view was active: ' + JSON.stringify(early[0]));
        const last = trace.filter(s => s.ready).pop(); assert.equal(last.ready, true); assert.equal(last.vis, 'visible');
        assert.ok(trace.length > 0, 'no frame was sampled');
        assert.ok(trace.filter(s => s.select).every(s => s.select === 'dark'), 'the Theme box showed "System" in a painted frame before the saved choice: ' + JSON.stringify(trace.map(s => s.select)));
        assert.ok(trace.filter(s => s.theme).every(s => s.theme === 'dark'), 'the saved theme was not applied from the first paint');
    });

    await t.test('every class and every method of every sample opens the right view, graph highlight and code', async () => {
        await fresh();
        for (const [name, file] of Object.entries(FILES)) {
            await b.openAssembly(file());
            const classes = await b.evaluate('UI.classSweep()'); expectNone(classes.bad, name + ': classes (' + classes.checked + ' checked)'); assert.ok(classes.checked > 0, name + ' has types');
            const methods = await b.evaluate('UI.methodSweep()'); expectNone(methods.bad, name + ': methods (' + methods.checked + ' checked)');
        }
        expectNone(await b.evaluate('UI.errors'), 'script errors');
    });

    await t.test('circular dependencies: warnings exactly where cycles are, right chains, right direction, selection kept, Back works; no false cycles elsewhere', async () => {
        await b.openAssembly(FILES.Circular());
        const r = await b.evaluate('UI.cycleChecks()'); assert.ok(r.chains >= 7, 'the sample has its 7 cycle chains'); expectNone(r.bad, 'circular dependency checks');
        for (const [name, file] of Object.entries(FILES).filter(([n]) => n !== 'Circular')) {
            await b.openAssembly(file());
            const bad = await b.evaluate(`cycles.filter(c => c.body.some((m, k) => !(calleesOf.get(m) || []).includes(c.body[(k + 1) % c.body.length]))).length`);
            assert.equal(bad, 0, name + ': a reported cycle is not a real loop of calls');
            if (name !== 'Vulnerable') assert.equal(await b.evaluate('cycles.length'), 0, name + ' reports a cycle it should not');
        }
    });

    await t.test('navigation sequences: class, inheritance, methods, code, cycles, related class, back and forward', async () => {
        await b.openAssembly(FILES.Inheritance());
        let r = await b.evaluate('(async () => { const s = await UI.sequences(); await s.inhSequences(); return s.result(); })()'); expectNone(r.bad, 'inheritance sequences'); assert.ok(r.checks >= 6, r.checks + ' checks ran');
        await b.openAssembly(FILES.Circular());
        r = await b.evaluate('(async () => { const s = await UI.sequences(); await s.cycSequences(); return s.result(); })()'); expectNone(r.bad, 'cycle sequences'); assert.ok(r.checks >= 6, r.checks + ' checks ran');
    });

    await t.test('every tab opens its view, and another assembly never shows the previous one', async () => {
        await b.openAssembly(FILES.Risky());
        const before = await b.evaluate(`(async () => { await UI.expandAll(); UI.row([...typeNodes.keys()][0]).click(); await UI.w(300); return document.getElementById('riskyBadge').textContent.trim(); })()`);
        await b.openAssembly(FILES.Metrics());
        const r = await b.evaluate(`(async () => { const bad = [], want = { tabOverview: 'overview', tabNavigator: 'navigator', tabCycles: 'cycles', tabRisky: 'risky', tabVulns: 'vulns', tabCompare: 'compare' };
            const s0 = UI.snap(); if (s0.type !== null || s0.method !== null || s0.view !== 'overview' || s0.ovTab !== 'summary') bad.push('a new assembly keeps the old selection or view: ' + JSON.stringify(s0));
            for (const [tab, view] of Object.entries(want)) { document.getElementById(tab).click(); await UI.w(300); if (UI.snap().view !== view || !document.getElementById(tab).classList.contains('active')) bad.push(tab + ' did not open ' + view); }
            document.getElementById('tabOverview').click(); await UI.w(200);
            if (!/MetricsDemo/.test(document.getElementById('overviewScroll').innerText) || /RiskyApis/.test(document.getElementById('overviewScroll').innerText)) bad.push('the Overview shows the wrong assembly');
            for (const tab of ['Architecture', 'Metrics']) { document.getElementById('ovTab' + tab).click(); await UI.w(400); if (/RiskyApis|TrustEverything/.test(document.querySelector('.view.active').innerText)) bad.push(tab + ' shows the previous assembly'); }
            if (document.getElementById('riskyBadge').textContent.trim() === ${JSON.stringify(before)} && ${JSON.stringify(before)} !== '-') bad.push('the Risky APIs badge did not change');
            document.getElementById('tabNavigator').click(); await UI.w(300); if (!/Select a type/i.test(document.querySelector('.view.active').innerText)) bad.push('the Navigator shows a stale selection');
            return bad; })()`);
        expectNone(r, 'navigation');
    });

    await t.test('search: partial, exact, case, namespace, methods, empty, no result, special characters; the selection is never changed', async () => {
        await b.openAssembly(FILES.Inheritance());
        const r = await b.evaluate('UI.searchChecks()'); expectNone(r.bad, 'search'); assert.ok(r.checks >= 12);
    });

    await t.test('bad files: a clear message, the loaded assembly and the screen stay intact', async () => {
        await b.openAssembly(FILES.Inheritance());
        const inh = readFileSync(FILES.Inheritance());
        const files = { 'notes.dll': Buffer.from('not a dll, just text\n'), 'empty.dll': Buffer.alloc(0), 'broken.dll': inh.subarray(0, 3000), 'readme.txt': Buffer.from('text') };
        const launcher = [join(root, 'bin', 'Debug', 'net8.0', 'dotnet-Assembly-Visualizer.exe'), join(root, 'bin', 'Debug', 'net8.0', 'dotnet-Assembly-Visualizer')].find(existsSync);
        if (launcher) files['dotnet-Assembly-Visualizer.exe'] = readFileSync(launcher);
        const expected = { 'notes.dll': /not a valid \.NET assembly/, 'empty.dll': /empty/, 'broken.dll': /not a valid \.NET assembly/, 'readme.txt': /No \.NET assembly/, 'dotnet-Assembly-Visualizer.exe': /native launcher/ };
        for (const [name, bytes] of Object.entries(files)) {
            const p = join(scratch, name); writeFileSync(p, bytes);
            await b.setFiles('#dllFile', [p]);
            const r = await b.evaluate('UI.badInput()');
            assert.match(r.toast, expected[name], name + ': ' + r.toast);
            assert.equal(r.keptAssembly, true, name + ' replaced or damaged the loaded assembly'); assert.equal(r.loaderStuck, false, name + ' left the loader on screen');
        }
    });

    await t.test('themes: switching keeps the view, selection and code, and redraws the graph', async () => {
        await b.openAssembly(FILES.Circular());
        expectNone(await b.evaluate(`UI.themeSwitchKeepsState('Samples.Cycles.Left')`), 'theme switching');
    });

    await t.test('themes: the system theme follows the operating system live, and the choice survives a reload', async () => {
        await b.colorScheme('light'); await b.evaluate("Theme.set('system'); 1");
        const at = (expected, label) => b.evaluate(`UI.until(() => document.documentElement.dataset.theme === '${expected}', 4000).then(v => !!v)`).then(ok => assert.equal(ok, true, label));
        await at('light', 'system light -> light');
        await b.colorScheme('dark'); await at('dark', 'the OS switched to dark while the app was open on System');
        await b.colorScheme('light'); await at('light', 'the OS switched back to light');
        await b.evaluate("Theme.set('light'); 1"); await b.colorScheme('dark'); await new Promise(r => setTimeout(r, 500));
        assert.equal(await b.evaluate('document.documentElement.dataset.theme'), 'light', 'an explicit Light choice must ignore the OS');
        await b.evaluate("Theme.set('dark'); 1"); await b.reload(); await b.injectHelpers();
        assert.deepEqual(await b.evaluate("[document.documentElement.dataset.theme, document.documentElement.dataset.themePref, document.getElementById('themeSelect').value]"), ['dark', 'dark', 'dark']);
        await b.colorScheme('light'); await b.evaluate("Theme.set('system'); 1");
    });

    await t.test('readability: all text on every screen is readable in light and in dark', async () => {
        await b.openAssembly(FILES.Circular());
        for (const theme of ['dark', 'light']) { const res = await b.evaluate(`UI.themeAudit('${theme}', 'Samples.Cycles.Left')`); expectNone(Object.entries(res).map(([screen, list]) => theme + ' / ' + screen + ': ' + list.join(', ')), 'low contrast'); }
    });

    await t.test('layout: no sideways page scroll or overflowing controls from desktop to phone width', async () => {
        await b.openAssembly(FILES.Circular());
        const allowed = /^(metricsScroll|src-code)/;                         // wide tables and code scroll inside their own panel
        for (const [w, h] of [[1400, 850], [1024, 700], [760, 600], [375, 812]]) {
            await b.viewport(w, h); await new Promise(r => setTimeout(r, 300));
            const res = await b.evaluate(`UI.layout('Samples.Cycles.Left')`);
            const problems = Object.entries(res).flatMap(([screen, o]) => [...(o.page ? ['page scrolls sideways'] : []), ...o.overflow, ...o.sideways.filter(s => !allowed.test(s))].map(x => `${w}x${h} / ${screen}: ${x}`));
            expectNone(problems, 'layout');
        }
        await b.viewport(1400, 850);
    });

    await t.test('a large real library (Newtonsoft.Json): fast, complete, scrolling code, big graphs', async (st) => {
        const big = bigLibrary(); if (!big) return st.skip('Newtonsoft.Json.dll not found next to the test project');
        await b.openAssembly(big);
        const r = await b.evaluate('UI.bigAssembly()', 180000);
        assert.ok(r.types > 200 && r.methods > 2000, `${r.types} types, ${r.methods} methods`);
        assert.ok(r.cycleRows === r.cycles && r.cycles > 0, 'the cycle list shows every chain');
        assert.ok(r.codeLength > 20000 && r.highlighted > 100 && r.codeScrolls, 'a very long class: code shown, highlighted and scrollable');
        assert.ok(r.wholeGraphNodes > 10, 'the whole-assembly inheritance graph is drawn');
        assert.ok(r.searchRows >= 100 && /100\+/.test(r.searchHeader), 'a big search result is capped and says so: ' + r.searchHeader);
        for (const k of ['biggestClassMs', 'wholeGraphMs', 'architectureMs', 'metricsMs', 'cyclesMs']) assert.ok(r[k] < 10000, `${k} took ${r[k]} ms`);
        expectNone(await b.evaluate('UI.errors'), 'script errors');
    });

    expectNone(b.pageErrors, 'uncaught exceptions during the whole run');
});
