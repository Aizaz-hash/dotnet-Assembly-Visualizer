const test = require('node:test');
const assert = require('node:assert/strict');
const M = require('../../wwwroot/js/overview-model.js');

const signed = { name: 'Lib', version: '1.2.0.0', fullName: 'Lib, Version=1.2.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089', culture: 'neutral', kind: 'Library (.dll)', platform: 'Any CPU',
    targetFramework: '.NETCoreApp,Version=v8.0', strongNamed: true, publicKeyToken: 'b77a5c561934e089', publicKeyBits: 1024, mvid: '11111111-2222-3333-4444-555555555555',
    debugBuild: false, hasEntryPoint: false, references: 4, sizeBytes: 20480 };
const rows = (a) => Object.fromEntries(M.identityRows(a).map(r => [r[0], r[1]]));

test('identity: name, version, full name, framework, key token and module id are listed', () => {
    const r = rows(signed);
    assert.equal(r['Name'], 'Lib');
    assert.equal(r['Full name'], signed.fullName);
    assert.equal(r['Target framework'], '.NETCoreApp,Version=v8.0');
    assert.match(r['Strong name'], /public key token b77a5c561934e089, 1024-bit key/);
    assert.equal(r['Module id (MVID)'], signed.mvid);
    assert.equal(r['File size'], '20.0 KB');
});

test('an unsigned assembly says so, and a guessed framework is marked as a guess', () => {
    const r = rows({ ...signed, strongNamed: false, publicKeyToken: null, targetFrameworkInferred: true });
    assert.match(r['Strong name'], /^no/);
    assert.match(r['Target framework'], /guessed from its references/);
    assert.match(rows({ ...signed, targetFramework: null })['Target framework'], /not recorded/);
});

test('facts the file does not have are left out, and no analysis gives no rows', () => {
    const r = rows({ name: 'X', version: '1.0.0.0' });
    assert.ok(!('File size' in r) && !('Module id (MVID)' in r) && !('References' in r));
    assert.deepEqual(M.identityRows(null), []);
});

const base = { types: 12, methods: 80, namespaces: 3, cycles: 0, risky: 0, metrics: { averageComplexity: 2.1, maxComplexity: 9 }, unreferenced: 4 };
const card = (s, key) => M.glanceCards(s).find(c => c.key === key);

test('every glance card leads to a view that exists', () => {
    const V = require('../../wwwroot/js/views.js');
    for (const vuln of [{ state: 'none' }, { state: 'scanning' }, { state: 'done', open: 2, urgent: 1 }])
        for (const c of M.glanceCards({ ...base, vuln })) assert.ok(V.get(c.view), c.key + ' -> ' + c.view);
});

test('the vulnerability card follows the background scan: not scanned, scanning, result', () => {
    assert.match(card({ ...base, vuln: { state: 'none' } }, 'vulns').hint, /not scanned yet/);
    assert.equal(card({ ...base, vuln: { state: 'scanning' } }, 'vulns').tone, 'busy');
    const done = card({ ...base, vuln: { state: 'done', open: 3, urgent: 2 } }, 'vulns');
    assert.equal(done.num, 3); assert.equal(done.tone, 'bad'); assert.match(done.hint, /2 critical \/ high/);
    assert.equal(card({ ...base, vuln: { state: 'done', open: 1, urgent: 0 } }, 'vulns').tone, '');
});

test('cycles and risky calls are highlighted only when there are some; older analyses without metrics still render', () => {
    assert.equal(card(base, 'cycles').tone, '');
    assert.equal(card({ ...base, cycles: 2, risky: 5 }, 'cycles').tone, 'warn');
    assert.equal(card({ ...base, risky: 5 }, 'risky').tone, 'warn');
    assert.equal(card({ ...base, metrics: null }, 'complexity'), undefined);
    assert.equal(card({ ...base, metrics: { averageComplexity: 9, maxComplexity: 30 } }, 'complexity').tone, 'warn');
});
