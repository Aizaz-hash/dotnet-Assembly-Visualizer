const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const P = require('../../wwwroot/js/scan-policy.js');

const file = (name, size = 100, lastModified = 1) => ({ name, size, lastModified });
const NOW = 10_000_000;
const base = () => ({ hasAssembly: true, serverEnabled: true, autoEnabled: true, consent: true, scanning: false, report: null, reportKey: null, key: 'k', now: NOW, lastFinishedAt: 0, lastOutcome: null, lastKey: null });
const decide = (over) => P.decideAutoScan({ ...base(), ...over });
const goodReport = { databaseReachable: true, complete: true };

test('a new input is scanned: once an assembly is open, scanning is allowed and nothing was scanned before', () => {
    assert.deepEqual(decide({}), { run: true, reason: 'run' });
});

test('nothing starts without an assembly, when the server switched scanning off, when the user turned it off, or without consent', () => {
    assert.equal(decide({ hasAssembly: false }).reason, 'no-assembly');
    assert.equal(decide({ serverEnabled: false }).reason, 'server-off');
    assert.equal(decide({ autoEnabled: false }).reason, 'disabled');
    assert.equal(decide({ consent: false }).reason, 'no-consent');
    assert.ok([decide({ hasAssembly: false }), decide({ serverEnabled: false }), decide({ autoEnabled: false }), decide({ consent: false })].every(d => !d.run));
});

test('a scan that is running is never started twice', () => {
    assert.deepEqual(decide({ scanning: true }), { run: false, reason: 'already-scanning' });
});

test('a recent complete result for the same input is reused; a different input or an old result is scanned again', () => {
    const fresh = { report: goodReport, reportKey: 'k', lastFinishedAt: NOW - 60_000, lastOutcome: 'ok', lastKey: 'k' };
    assert.deepEqual(decide(fresh), { run: false, reason: 'fresh-result' });
    assert.equal(decide({ ...fresh, key: 'other' }).run, true, 'a changed file or manifest is a different scan');
    assert.equal(decide({ ...fresh, lastFinishedAt: NOW - P.RESULT_VALID_MS - 1 }).run, true, 'an hour later the advisories may have changed');
});

test('"nothing to check" results are reusable too (no repeated work for an assembly with no packages)', () => {
    const r = { report: { databaseReachable: false, nothingToCheck: true, complete: true }, reportKey: 'k', lastFinishedAt: NOW - 1000, lastOutcome: 'ok', lastKey: 'k' };
    assert.equal(decide(r).reason, 'fresh-result');
});

test('a result that could not reach the database is not "fresh"', () => {
    const r = { report: { databaseReachable: false, complete: false }, reportKey: 'k', lastFinishedAt: NOW - 1000, lastOutcome: 'incomplete', lastKey: 'k' };
    assert.notEqual(decide(r).reason, 'fresh-result');
});

test('after a failure or an incomplete scan it does not hammer the network, but tries again after a while', () => {
    const failed = { lastOutcome: 'failed', lastKey: 'k', lastFinishedAt: NOW - 30_000 };
    assert.deepEqual(decide(failed), { run: false, reason: 'recent-failure' });
    assert.equal(decide({ ...failed, lastFinishedAt: NOW - P.RETRY_AFTER_FAILURE_MS - 1 }).run, true);
    assert.equal(decide({ ...failed, key: 'changed' }).run, true, 'a new input is never blocked by an old failure');
    assert.equal(decide({ lastOutcome: 'incomplete', lastKey: 'k', lastFinishedAt: NOW - 1000 }).reason, 'recent-failure');
});

test('the key changes with the file, the manifest and the option that adds the tool\'s own packages', () => {
    const a = P.scanKey(file('A.dll'), null, false);
    assert.equal(a, P.scanKey(file('A.dll'), null, false), 'same input, same key');
    assert.notEqual(a, P.scanKey(file('A.dll', 101), null, false), 'rebuilt file (size)');
    assert.notEqual(a, P.scanKey(file('A.dll', 100, 2), null, false), 'rebuilt file (date)');
    assert.notEqual(a, P.scanKey(file('B.dll'), null, false), 'another assembly');
    assert.notEqual(a, P.scanKey(file('A.dll'), file('A.deps.json'), false), 'a manifest gives exact versions: a different scan');
    assert.notEqual(a, P.scanKey(file('A.dll'), null, true), 'including the tool\'s own packages');
    assert.doesNotThrow(() => P.scanKey(undefined, undefined, false));
});

test('the result line says what happened without overstating it', () => {
    assert.match(P.describeResult('A.dll', { databaseReachable: true, complete: true, summary: { total: 0 } }), /no known vulnerabilities\./);
    assert.match(P.describeResult('A.dll', { databaseReachable: true, complete: false, summary: { total: 0 } }), /incomplete/);
    assert.match(P.describeResult('A.dll', { databaseReachable: false, complete: false, summary: { total: 0 } }), /could not be reached - not a clean result/);
    assert.match(P.describeResult('A.dll', { databaseReachable: true, complete: true, summary: { total: 3, critical: 1, high: 1 } }), /3 findings \(2 critical \/ high\)/);
    assert.match(P.describeResult('A.dll', { databaseReachable: false, nothingToCheck: true, complete: true, summary: { total: 0 } }), /no NuGet packages to check/);
    assert.match(P.describeResult('A.dll', null), /did not finish/);
});

// ------------------------------------------------------------------ the wiring uses the policy (structure checks)

const root = path.join(__dirname, '..', '..', 'wwwroot');
const read = (...p) => fs.readFileSync(path.join(root, ...p), 'utf8').replace(/\r\n/g, '\n');

test('one scan function serves both the button and the automatic start, and an assembly change schedules a scan', () => {
    const vuln = read('js', 'vuln.js');
    assert.equal((vuln.match(/fetch\('\/api\/vulnerabilities\/scan'/g) || []).length, 2, 'two places send a scan request: an assembly and a dependency file on its own');
    assert.match(vuln, /await scanAssembly\(currentAssemblyName, \{ auto: false \}\)/);
    assert.match(vuln, /scanAssembly\(name, \{ auto: true \}\)/);
    assert.match(vuln, /function vulnOnAssemblyChanged\(\)[\s\S]*?vulnScheduleAutoScan\(\);\n\}/);
    assert.match(vuln, /ScanPolicy\.decideAutoScan/);
});

test('the page loads the policies before the scripts that use them', () => {
    const html = read('index.html');
    const order = [...html.matchAll(/<script src="js\/([\w-]+\.js)"><\/script>/g)].map(m => m[1]);
    for (const dep of ['notify-policy.js', 'scan-policy.js']) assert.ok(order.indexOf(dep) >= 0 && order.indexOf(dep) < order.indexOf('app.js'), dep + ' loads before app.js');
    assert.ok(html.includes('id="toastHost"') && !html.includes('id="msgWindow"'), 'toasts replace the full-width message bar');
});

test('notifications are toasts: a fixed-width box on the right, not a bar across the window', () => {
    const css = read('css', 'styles.css');
    assert.match(css, /#toastHost \{[^}]*position: fixed;[^}]*right: 12px;[^}]*width: min\(340px/);
    assert.ok(!/#msgWindow/.test(css), 'the old full-width bar styles are gone');
});
