const test = require('node:test');
const assert = require('node:assert/strict');
const zlib = require('node:zlib');
const P = require('../../wwwroot/js/package-input.js');

// minimal zip writer (stored or deflate) so the reader is tested against real archive bytes
function makeZip(files, { deflate = true } = {}) {
    const parts = [], central = [];
    let offset = 0;
    for (const [name, content] of Object.entries(files)) {
        const data = Buffer.from(content);
        const comp = deflate ? zlib.deflateRawSync(data) : data;
        const method = deflate ? 8 : 0;
        const nameBuf = Buffer.from(name);
        const crc = zlib.crc32(data);
        const local = Buffer.alloc(30);
        local.writeUInt32LE(0x04034b50, 0); local.writeUInt16LE(20, 4); local.writeUInt16LE(0, 6); local.writeUInt16LE(method, 8);
        local.writeUInt32LE(crc, 14); local.writeUInt32LE(comp.length, 18); local.writeUInt32LE(data.length, 22); local.writeUInt16LE(nameBuf.length, 26);
        parts.push(local, nameBuf, comp);
        const c = Buffer.alloc(46);
        c.writeUInt32LE(0x02014b50, 0); c.writeUInt16LE(20, 4); c.writeUInt16LE(20, 6); c.writeUInt16LE(method, 10);
        c.writeUInt32LE(crc, 16); c.writeUInt32LE(comp.length, 20); c.writeUInt32LE(data.length, 24); c.writeUInt16LE(nameBuf.length, 28); c.writeUInt32LE(offset, 42);
        central.push(c, nameBuf);
        offset += 30 + nameBuf.length + comp.length;
    }
    const cd = Buffer.concat(central);
    const end = Buffer.alloc(22);
    end.writeUInt32LE(0x06054b50, 0); end.writeUInt16LE(Object.keys(files).length, 8); end.writeUInt16LE(Object.keys(files).length, 10);
    end.writeUInt32LE(cd.length, 12); end.writeUInt32LE(offset, 16);
    return new Blob([Buffer.concat([...parts, cd, end])]);
}

test('reads stored and deflated entries and skips directories', async () => {
    for (const deflate of [true, false]) {
        const zip = makeZip({ 'lib/net8.0/A.dll': 'AAAA'.repeat(500), 'A.nuspec': '<package/>', 'lib/': '' }, { deflate });
        const entries = await P.readZipEntries(zip);
        assert.deepEqual(entries.map(e => e.path), ['lib/net8.0/A.dll', 'A.nuspec']);
        assert.equal(await (await entries[0].read()).text(), 'AAAA'.repeat(500));
        assert.equal(await (await entries[1].read()).text(), '<package/>');
    }
});

test('rejects non-zip and damaged input with a readable message', async () => {
    await assert.rejects(P.readZipEntries(new Blob(['this is not a zip file at all'])), /not a valid zip/);
    const zip = makeZip({ 'a.dll': 'x' });
    const bytes = Buffer.from(await zip.arrayBuffer());
    bytes.writeUInt32LE(0xdeadbeef, bytes.length - 22 + 16);       // central directory offset points nowhere valid
    await assert.rejects(P.readZipEntries(new Blob([bytes])), /damaged/);
});

test('a size mismatch is reported instead of returning wrong bytes', async () => {
    const zip = makeZip({ 'a.dll': 'hello world' });
    const bytes = Buffer.from(await zip.arrayBuffer());
    const cdStart = bytes.readUInt32LE(bytes.length - 22 + 16);
    bytes.writeUInt32LE(999, cdStart + 24);                        // claim a different uncompressed size
    const [entry] = await P.readZipEntries(new Blob([bytes]));
    await assert.rejects(entry.read(), /size mismatch/);
});

const E = (...paths) => paths.map(p => ({ path: p, size: 1 }));

test('a package loads only the best lib/<tfm> folder and ignores ref, runtimes and resources', () => {
    const r = P.plan(E('Foo.nuspec', 'lib/netstandard2.0/Foo.dll', 'lib/net8.0/Foo.dll', 'lib/net48/Foo.dll', 'ref/net8.0/Foo.dll',
        'runtimes/win/native/x.dll', 'lib/net8.0/de/Foo.resources.dll', 'Foo.deps.json'));
    assert.equal(r.kind, 'package');
    assert.deepEqual(r.assemblies, ['lib/net8.0/Foo.dll']);
    assert.ok(r.skipped.some(s => s.path === 'lib/net48/Foo.dll' && /other target framework/.test(s.reason)));
    assert.ok(r.skipped.some(s => s.path === 'ref/net8.0/Foo.dll'));
});

test('net48 loses to netstandard2.0, which loses to net6.0', () => {
    assert.ok(P.tfmRank('net6.0') > P.tfmRank('netstandard2.1'));
    assert.ok(P.tfmRank('netstandard2.0') > P.tfmRank('net48'));
    assert.ok(P.tfmRank('net48') > P.tfmRank('unknown1'));
});

test('a publish folder loads the app assemblies, not the copied framework, and pairs the deps.json', () => {
    const r = P.plan(E('MyApp.dll', 'MyApp.deps.json', 'MyApp.Core.dll', 'System.Private.CoreLib.dll', 'System.Linq.dll', 'Newtonsoft.Json.dll', 'hostfxr.dll', 'MyApp.runtimeconfig.json'));
    assert.deepEqual([...r.assemblies].sort(), ['MyApp.Core.dll', 'MyApp.dll', 'Newtonsoft.Json.dll']);
    assert.equal(r.assemblies[r.assemblies.length - 1], 'MyApp.dll');                // the main assembly is opened last, so it is the active one
    assert.equal(r.deps['MyApp.dll'], 'MyApp.deps.json');
    assert.equal(r.deps['Newtonsoft.Json.dll'], 'MyApp.deps.json');                  // same folder: the app's manifest gives exact versions
    assert.ok(r.skipped.some(s => s.path === 'System.Linq.dll' && /framework/.test(s.reason)));
});

test('a folder made only of System.* assemblies is still opened', () => {
    const r = P.plan(E('System.Foo.dll', 'System.Bar.dll'));
    assert.equal(r.assemblies.length, 2);
});

test('the limit is applied and reported', () => {
    const files = Array.from({ length: 12 }, (_, i) => `Lib${String(i).padStart(2, '0')}.dll`);
    const r = P.plan(E(...files), { max: 8 });
    assert.equal(r.assemblies.length, 8);
    assert.equal(r.skipped.filter(s => /limit of 8/.test(s.reason)).length, 4);
});

test('an exe is the main assembly when no deps.json names one; windows separators are accepted', () => {
    const r = P.plan(E('bin\\Tool.exe', 'bin\\Helper.dll'));
    assert.equal(r.assemblies[r.assemblies.length - 1], 'bin/Tool.exe');
});

test('a single deps.json applies to every opened assembly; nothing to open gives an empty plan', () => {
    const r = P.plan(E('A.dll', 'B.dll', 'app.deps.json'));
    assert.equal(r.deps['A.dll'], 'app.deps.json');
    assert.equal(r.deps['B.dll'], 'app.deps.json');
    const none = P.plan(E('readme.txt', 'notes.md'));
    assert.deepEqual(none.assemblies, []);
});

test('project.assets.json and packages.lock.json are picked up; deps.json wins when several exist', () => {
    const only = P.plan(E('src/App.dll', 'src/obj/project.assets.json'));
    assert.equal(only.deps['src/App.dll'], 'src/obj/project.assets.json');

    const lock = P.plan(E('App.dll', 'packages.lock.json'));
    assert.equal(lock.deps['App.dll'], 'packages.lock.json');

    const all = P.plan(E('App.dll', 'App.deps.json', 'project.assets.json', 'packages.lock.json'));
    assert.equal(all.deps['App.dll'], 'App.deps.json');

    const noDeps = P.plan(E('App.dll', 'project.assets.json', 'packages.lock.json'));
    assert.equal(noDeps.deps['App.dll'], 'project.assets.json');                      // assets (full graph) before the lock file
});

test('a manifest in an unrelated folder is not attached when several exist', () => {
    const r = P.plan(E('a/A.dll', 'b/B.deps.json', 'c/other.deps.json'));
    assert.equal(r.deps['a/A.dll'], undefined);
});
