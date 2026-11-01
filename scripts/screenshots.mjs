// Takes the screenshots used in the README (docs/images/v3-*.png) from the real app, in a headless Edge / Chrome.
//
//   node scripts/screenshots.mjs
//
// Same needs as the UI test (tests/ui): the app built, the sample assemblies built (run `dotnet test tests/DotNetVisualizer.Web.Tests` once) and a Chromium browser.
// The window is 1400 x 1000 and the system theme is light; the dark screenshot switches the theme in the page. Vulnerability scanning is switched off for the run, so the
// vulnerability screenshots (docs/images/v2-vuln-*.png, which need the network) are not retaken.
import { existsSync, mkdirSync, writeFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { root, browserPath, startApp, startBrowser } from '../tests/ui/browser.mjs';

const samples = [process.env.UI_SAMPLES, ...['Debug', 'Release'].map(c => join(root, 'tests', 'DotNetVisualizer.Web.Tests', 'bin', c, 'net8.0', 'samples'))].filter(Boolean).find(d => existsSync(join(d, 'Inheritance.dll')));
if (!browserPath) throw new Error('no Chromium browser found (set UI_BROWSER)');
if (!samples) throw new Error('sample assemblies not built (run: dotnet test tests/DotNetVisualizer.Web.Tests)');
const out = join(dirname(fileURLToPath(import.meta.url)), '..', 'docs', 'images'); mkdirSync(out, { recursive: true });

const app = await startApp(), b = await startBrowser();
try {
    await b.viewport(1400, 1000); await b.colorScheme('light');
    await b.goto(app.url + '/index.html'); await b.evaluate("try { localStorage.clear(); sessionStorage.clear(); } catch (e) {} 1"); await b.reload(); await b.injectHelpers();

    const shot = async (name) => {
        await new Promise(r => setTimeout(r, 700));                                  // let graphs settle
        const { data } = await b.send('Page.captureScreenshot', { format: 'png' });
        writeFileSync(join(out, name), Buffer.from(data, 'base64')); console.log('wrote docs/images/' + name);
    };
    const go = (screen, cls) => b.evaluate(`UI.goTo(${JSON.stringify(screen)}, ${JSON.stringify(cls)}).then(() => 1)`);
    const open = (file) => b.openAssembly(join(samples, file));

    // the introduction, before anything is open
    await b.evaluate("UI.until(() => document.body.classList.contains('app-ready'))"); await shot('v3-start.png');

    await open('CircularDependencies.dll');
    await go('overview', 'Samples.Cycles.Left'); await shot('v3-overview.png');
    await go('architecture', 'Samples.Cycles.Left'); await shot('v3-architecture.png');
    await go('metrics', 'Samples.Cycles.Left'); await shot('v3-metrics.png');
    await go('navigator-method', 'Samples.Cycles.Left'); await shot('v3-navigator-method.png');
    await b.evaluate("navigate({ view: 'cycles', type: null, method: null, cycleAll: true }); UI.w(900).then(() => 1)"); await shot('v3-cycles.png');
    await b.evaluate("navigate({ view: 'navigator' }); UI.w(300).then(() => 1)");     // picking a method keeps the open view, so go to the Navigator first
    await go('navigator-method', 'Samples.Cycles.Left');
    await b.evaluate("Theme.set('dark'); UI.w(900).then(() => 1)"); await shot('v3-dark.png');
    await b.evaluate("Theme.set('system'); UI.w(500).then(() => 1)");

    await open('Inheritance.dll');
    await b.evaluate("selectClass('Samples.Inheritance.Shape'); const d = document.getElementById('inhDepth'); d.value = '2'; d.dispatchEvent(new Event('change')); UI.until(() => UI.visibleCode(), 10000).then(() => 1)");
    await shot('v3-navigator-class.png');

    await open('RiskyApis.dll');
    await b.evaluate("navigate({ view: 'risky' }); UI.w(900).then(() => 1)"); await shot('v3-risky.png');
} finally { await b.close(); app.stop(); }
