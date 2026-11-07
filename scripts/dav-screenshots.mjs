// Makes the screenshots of the Dav commands for docs/dav-module.md: runs each command for real in PowerShell, puts the
// output on a terminal-looking page and takes a picture of it with headless Chrome or Edge.
//
//   node scripts/dav-screenshots.mjs --module artifacts/Dav --samples tests/DotNetVisualizer.Web.Tests/bin/Release/net8.0/samples
//
// Needs: PowerShell 7 (pwsh on the PATH, or DAV_PWSH), Chrome or Edge (or CHROME), and the module built by scripts/build-module.ps1.
import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';

const arg = (name, fallback) => { const i = process.argv.indexOf('--' + name); return i > 0 ? process.argv[i + 1] : fallback; };
const module = resolve(arg('module', 'artifacts/Dav'));
const samples = resolve(arg('samples', 'tests/DotNetVisualizer.Web.Tests/bin/Release/net8.0/samples'));
const out = resolve(arg('out', 'docs/images'));
const pwsh = process.env.DAV_PWSH || 'pwsh';

const chromeCandidates = process.platform === 'win32'
    ? ['C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', 'C:/Program Files/Microsoft/Edge/Application/msedge.exe', 'C:/Program Files/Google/Chrome/Application/chrome.exe', 'C:/Program Files (x86)/Google/Chrome/Application/chrome.exe']
    : process.platform === 'darwin'
        ? ['/Applications/Google Chrome.app/Contents/MacOS/Google Chrome', '/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge']
        : ['/usr/bin/google-chrome', '/usr/bin/google-chrome-stable', '/usr/bin/chromium', '/usr/bin/chromium-browser', '/usr/bin/microsoft-edge'];
const chrome = process.env.CHROME || chromeCandidates.find(existsSync);
if (!chrome) throw new Error('No Chrome or Edge found; set CHROME.');
if (!existsSync(join(module, 'Dav.psd1'))) throw new Error(`No module at ${module}; run scripts/build-module.ps1 first.`);

// what is typed (shown after the prompt), and what is run; paths are relative to the samples folder so the pictures stay short
const cases = [
    { file: 'dav-risky', command: 'Get-DavRiskyApi ./RiskyApis.dll -FailOn high | Format-Table Severity, RuleId, Api -AutoSize' },
    { file: 'dav-threshold', command: 'Get-DavRiskyApi ./RiskyApis.dll -FailOn high -Allow deserialize-binaryformatter | Measure-Object | Select-Object Count' },
    { file: 'dav-cycles', maxLines: 16, command: 'Get-DavCircularDependency ./CircularDependencies.dll' },
    { file: 'dav-compare', command: 'Compare-DavVersion ./api-compat/v2/ApiCompat.dll ./api-compat/v1/ApiCompat.dll | Format-List Verdict, RequiredBump, ActualBump, VersionMismatch, OldApiFingerprint, NewApiFingerprint' },
    { file: 'dav-check', command: 'Invoke-DavCheck ./CircularDependencies.dll -SkipVulnerabilities -WarningAction SilentlyContinue | Format-List Passed, Reasons' },
    { file: 'dav-check-fail', command: 'Invoke-DavCheck ./RiskyApis.dll -SkipVulnerabilities -FailOn high | Out-Null' },
    { file: 'dav-invalid', command: 'Get-DavRiskyApi ./RiskyApis.dll -FailOn severe' }
];

const esc = s => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
const work = mkdtempSync(join(tmpdir(), 'dav-shots-'));
mkdirSync(out, { recursive: true });

for (const c of cases) {
    const script = [
        '$PSStyle.OutputRendering = "PlainText"',
        '$ErrorView = "ConciseView"',
        `Import-Module '${join(module, 'Dav.psd1').replace(/'/g, "''")}'`,
        `$ErrorActionPreference = 'Continue'`,
        `try { ${c.command} } catch { Write-Host ($_.Exception.Message) }`
    ].join('\n');
    const run = spawnSync(pwsh, ['-NoProfile', '-NonInteractive', '-Command', script], { cwd: samples, encoding: 'utf8' });
    // warnings and errors go to the same picture, in the order PowerShell printed them
    let text = (run.stdout + (run.stderr ? '\n' + run.stderr : '')).replace(/\r/g, '').replace(/^\n+/, '').trimEnd();
    text = text.replace(/^WARNING: /gm, 'WARNING: ');
    let shown = text.split('\n');
    if (c.maxLines && shown.length > c.maxLines) shown = [...shown.slice(0, c.maxLines), '...'];
    const lines = [`PS ${'D:\\build\\samples'}> ${c.command}`, '', ...shown];
    const wrapped = lines.flatMap(l => l.length <= 124 ? [l] : l.match(/.{1,124}/g));
    const height = Math.max(120, wrapped.length * 21 + 78);
    const html = `<!doctype html><meta charset="utf-8"><style>
html,body{margin:0;background:#e8ebf0}
.win{margin:18px;border-radius:10px;overflow:hidden;box-shadow:0 6px 24px rgba(0,0,0,.28);background:#012456;font:15px/21px Consolas,'Cascadia Mono','DejaVu Sans Mono',Menlo,monospace;color:#eeedf0}
.bar{background:#2b2b2b;color:#cfcfcf;font:13px/30px 'Segoe UI',system-ui,sans-serif;padding:0 14px}
pre{margin:0;padding:14px 18px 18px;white-space:pre;overflow:hidden}
.p{color:#ffe08a}.w{color:#ffd23f}.e{color:#ff8c8c}
</style><div class="win"><div class="bar">PowerShell ▸ Dav</div><pre>${wrapped.map((l, i) => {
        const e = esc(l);
        if (i === 0) return `<span class="p">${e.replace(/^(PS [^>]*&gt;)/, '$1')}</span>`;
        if (/^WARNING:/.test(l)) return `<span class="w">${e}</span>`;
        if (/^(Get-Dav|Compare-Dav|Invoke-Dav)|^\s*\+|Cannot validate|does not belong|risky API lead|Version mismatch|Check failed|INCOMPLETE/.test(l)) return `<span class="e">${e}</span>`;
        return e;
    }).join('\n')}</pre></div>`;
    const page = join(work, c.file + '.html');
    writeFileSync(page, html);
    const png = join(out, c.file + '.png');
    const url = 'file:///' + page.split(String.fromCharCode(92)).join('/');
    const take = tag => spawnSync(chrome, ['--headless=new', '--disable-gpu', '--no-first-run', `--user-data-dir=${join(work, `profile-${tag}-${c.file}`)}`,
        '--hide-scrollbars', `--window-size=1130,${height + 36}`, `--screenshot=${png}`, url], { encoding: 'utf8' });
    // the browser can return before it has written the file: wait for it, and try once more if it never shows up
    let shot = take('a');
    for (let attempt = 0; attempt < 2 && !existsSync(png); attempt++) {
        for (let i = 0; i < 50 && !existsSync(png); i++) Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 100);
        if (!existsSync(png)) shot = take('b' + attempt);
    }
    if (!existsSync(png)) throw new Error(`No screenshot for ${c.file}: ${shot.stderr}`);
    console.log(`${c.file}.png  (${wrapped.length} lines)`);
}
rmSync(work, { recursive: true, force: true });
