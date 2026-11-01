// Starts the app and a headless Chromium browser and drives it through the DevTools protocol (the WebSocket built into Node, no packages).
// Shared by the UI test (ui.test.mjs) and the screenshot script (scripts/screenshots.mjs).
import { spawn, spawnSync } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname, basename } from 'node:path';
import { fileURLToPath } from 'node:url';
import net from 'node:net';

const here = dirname(fileURLToPath(import.meta.url));
export const root = join(here, '..', '..');
const pageScript = readFileSync(join(here, 'page.js'), 'utf8');
/** The build configuration of the app that is started (UI_TEST_CONFIG, default Debug). */
export const config = process.env.UI_TEST_CONFIG || 'Debug';

export function findBrowser() {
    const env = process.env.UI_BROWSER; if (env && existsSync(env)) return env;
    const c = process.platform === 'win32'
        ? ['C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', 'C:/Program Files/Microsoft/Edge/Application/msedge.exe', 'C:/Program Files/Google/Chrome/Application/chrome.exe', 'C:/Program Files (x86)/Google/Chrome/Application/chrome.exe']
        : process.platform === 'darwin'
            ? ['/Applications/Google Chrome.app/Contents/MacOS/Google Chrome', '/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge']
            : ['/usr/bin/google-chrome', '/usr/bin/google-chrome-stable', '/usr/bin/chromium', '/usr/bin/chromium-browser', '/usr/bin/microsoft-edge'];
    return c.find(existsSync) || null;
}
export const browserPath = findBrowser();

// ---------------------------------------------------------------- the app

function freePort() { return new Promise((res, rej) => { const s = net.createServer(); s.listen(0, '127.0.0.1', () => { const p = s.address().port; s.close(() => res(p)); }); s.on('error', rej); }); }
function killTree(child) { if (!child || child.exitCode !== null) return; if (process.platform === 'win32') spawnSync('taskkill', ['/PID', String(child.pid), '/T', '/F']); else child.kill('SIGTERM'); }
async function waitFor(fn, ms, what) { const t0 = Date.now(); let last; while (Date.now() - t0 < ms) { try { const v = await fn(); if (v) return v; } catch (e) { last = e; } await new Promise(r => setTimeout(r, 200)); } throw new Error('timed out waiting for ' + what + (last ? ': ' + last.message : '')); }

export async function startApp() {
    if (process.env.UI_TEST_URL) return { url: process.env.UI_TEST_URL.replace(/\/$/, ''), stop() { } };
    const port = await freePort(), url = `http://127.0.0.1:${port}`;
    const out = join(root, 'bin', config, 'net8.0');
    // use the existing build when it is complete; otherwise (or with UI_TEST_BUILD=1) let dotnet run build it
    const built = existsSync(join(out, 'dotnet-Assembly-Visualizer.dll')) && existsSync(join(out, 'dotnet-Assembly-Visualizer.runtimeconfig.json')) && process.env.UI_TEST_BUILD !== '1';
    const args = ['run', '-c', config, ...(built ? ['--no-build'] : []), '--no-launch-profile', '--urls', url];
    // scanning is switched off so the test never needs the network
    const child = spawn('dotnet', args, { cwd: root, env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Production', Vulnerability__Enabled: 'false' }, stdio: ['ignore', 'pipe', 'pipe'] });
    let log = ''; child.stdout.on('data', d => log += d); child.stderr.on('data', d => log += d);
    try { await waitFor(async () => (await fetch(url + '/index.html')).ok, 180000, 'the app to start'); }
    catch (e) { killTree(child); throw new Error(e.message + '\n' + log.slice(-1500)); }
    return { url, stop() { killTree(child); } };
}

// ---------------------------------------------------------------- the browser (DevTools protocol over the built-in WebSocket)

export async function startBrowser() {
    const profile = mkdtempSync(join(tmpdir(), 'dav-ui-'));
    const args = ['--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check', '--disable-extensions', '--remote-debugging-port=0', `--user-data-dir=${profile}`, '--window-size=1400,850', ...(process.platform === 'linux' ? ['--no-sandbox'] : []), 'about:blank'];
    const proc = spawn(browserPath, args, { stdio: 'ignore' });
    const portFile = join(profile, 'DevToolsActivePort');
    const port = (await waitFor(() => existsSync(portFile) && readFileSync(portFile, 'utf8').split('\n')[0], 30000, 'the browser to start')).trim();
    let target = await fetch(`http://127.0.0.1:${port}/json/new?about:blank`, { method: 'PUT' }).then(r => r.json());
    const ws = new WebSocket(target.webSocketDebuggerUrl);
    await new Promise((res, rej) => { ws.onopen = res; ws.onerror = () => rej(new Error('could not connect to the browser')); });
    let id = 0; const pending = new Map(), listeners = [];
    ws.onmessage = (m) => { const msg = JSON.parse(m.data); if (msg.id && pending.has(msg.id)) { const { res, rej } = pending.get(msg.id); pending.delete(msg.id); msg.error ? rej(new Error(msg.error.message)) : res(msg.result); } else listeners.forEach(l => l(msg)); };
    const send = (method, params = {}) => new Promise((res, rej) => { const i = ++id; pending.set(i, { res, rej }); ws.send(JSON.stringify({ id: i, method, params })); });
    const pageErrors = [];
    listeners.push((m) => { if (m.method === 'Runtime.exceptionThrown') pageErrors.push(m.params.exceptionDetails.exception?.description || m.params.exceptionDetails.text); });
    await send('Page.enable'); await send('Runtime.enable'); await send('DOM.enable');

    const api = {
        pageErrors, send,
        async evaluate(expression, timeoutMs = 120000) {
            const run = send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
            const r = await Promise.race([run, new Promise((_, rej) => setTimeout(() => rej(new Error('page script timed out: ' + expression.slice(0, 80))), timeoutMs))]);
            if (r.exceptionDetails) throw new Error('page error: ' + (r.exceptionDetails.exception?.description || r.exceptionDetails.text));
            return r.result.value;
        },
        async goto(url) { const loaded = new Promise(res => { const l = (m) => { if (m.method === 'Page.loadEventFired') { listeners.splice(listeners.indexOf(l), 1); res(); } }; listeners.push(l); }); await send('Page.navigate', { url }); await loaded; },
        async reload() { const loaded = new Promise(res => { const l = (m) => { if (m.method === 'Page.loadEventFired') { listeners.splice(listeners.indexOf(l), 1); res(); } }; listeners.push(l); }); await send('Page.reload', { ignoreCache: true }); await loaded; },
        async setFiles(selector, files) { const doc = await send('DOM.getDocument', { depth: 0 }); const q = await send('DOM.querySelector', { nodeId: doc.root.nodeId, selector }); await send('DOM.setFileInputFiles', { files, nodeId: q.nodeId }); },
        async viewport(width, height) { await send('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: false }); },
        async colorScheme(value) { await send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-color-scheme', value }] }); },
        async injectHelpers() { await api.evaluate(pageScript); },
        /** Opens a file the way a user does (the Open button's file input) and waits until it is loaded. */
        async openAssembly(path) { await api.setFiles('#dllFile', [path]); return api.evaluate(`UI.waitAssembly(${JSON.stringify(basename(path))})`); },
        async close() { try { ws.close(); } catch { } if (process.platform === 'win32') spawnSync('taskkill', ['/PID', String(proc.pid), '/T', '/F']); else proc.kill('SIGKILL'); await new Promise(r => setTimeout(r, 400)); try { rmSync(profile, { recursive: true, force: true }); } catch { } }
    };
    return api;
}
