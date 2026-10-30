/*
 * Opening more than a single DLL: .nupkg / .zip archives and whole folders.
 *
 *  - readZipEntries: a small zip reader (stored + deflate) built on the browser's DecompressionStream, so no library is bundled.
 *  - plan: decides which entries to load as assemblies and which .deps.json belongs to which assembly.
 *
 * No DOM access; runs in the browser (window.PackageInput) and under node --test.
 */
(function (root, factory) {
    if (typeof module === 'object' && module.exports) module.exports = factory();
    else root.PackageInput = factory();
})(typeof self !== 'undefined' ? self : this, function () {
    'use strict';

    const MAX_ENTRIES = 5000;
    const MAX_ENTRY_BYTES = 250 * 1024 * 1024;       // per file, as stored in the archive once inflated

    // ---------------------------------------------------------------- zip

    function u16(v, o) { return v.getUint16(o, true); }
    function u32(v, o) { return v.getUint32(o, true); }

    /**
     * Lists the entries of a zip (a .nupkg is a zip). Each entry: { path, size, read(): Promise<Blob> }.
     * Throws an Error with a user-readable message for anything it cannot read (encrypted, zip64, damaged).
     */
    async function readZipEntries(blob) {
        const tailLen = Math.min(blob.size, 22 + 65535);
        const tail = new DataView(await blob.slice(blob.size - tailLen).arrayBuffer());
        let eocd = -1;
        for (let i = tailLen - 22; i >= 0; i--) {
            if (u32(tail, i) === 0x06054b50) { eocd = i; break; }
        }
        if (eocd < 0) throw new Error('this is not a valid zip / .nupkg file.');

        const total = u16(tail, eocd + 10);
        const cdSize = u32(tail, eocd + 12);
        const cdOffset = u32(tail, eocd + 16);
        if (total === 0xFFFF || cdSize === 0xFFFFFFFF || cdOffset === 0xFFFFFFFF) throw new Error('zip64 archives are not supported.');
        if (total > MAX_ENTRIES) throw new Error(`the archive has ${total} entries (limit ${MAX_ENTRIES}).`);

        const cd = new DataView(await blob.slice(cdOffset, cdOffset + cdSize).arrayBuffer());
        const decoder = new TextDecoder('utf-8');
        const entries = [];
        let p = 0;
        for (let n = 0; n < total; n++) {
            if (p + 46 > cd.byteLength || u32(cd, p) !== 0x02014b50) throw new Error('the archive directory is damaged.');
            const flags = u16(cd, p + 8), method = u16(cd, p + 10);
            const compSize = u32(cd, p + 20), size = u32(cd, p + 24);
            const nameLen = u16(cd, p + 28), extraLen = u16(cd, p + 30), commentLen = u16(cd, p + 32);
            const localOffset = u32(cd, p + 42);
            const path = decoder.decode(new Uint8Array(cd.buffer, cd.byteOffset + p + 46, nameLen));
            p += 46 + nameLen + extraLen + commentLen;

            if (path.endsWith('/')) continue;                      // directory
            entries.push({
                path, size,
                async read() {
                    if (flags & 1) throw new Error(`"${path}" is encrypted.`);
                    if (size > MAX_ENTRY_BYTES) throw new Error(`"${path}" is larger than ${MAX_ENTRY_BYTES / 1048576} MB once unpacked.`);
                    const head = new DataView(await blob.slice(localOffset, localOffset + 30).arrayBuffer());
                    if (u32(head, 0) !== 0x04034b50) throw new Error(`"${path}" is damaged.`);
                    const dataStart = localOffset + 30 + u16(head, 26) + u16(head, 28);
                    const raw = blob.slice(dataStart, dataStart + compSize);
                    if (method === 0) return raw;
                    if (method !== 8) throw new Error(`"${path}" uses an unsupported compression method (${method}).`);
                    const out = await new Response(raw.stream().pipeThrough(new DecompressionStream('deflate-raw'))).blob();
                    if (out.size !== size) throw new Error(`"${path}" is damaged (size mismatch).`);
                    return out;
                }
            });
        }
        return entries;
    }

    // ---------------------------------------------------------------- choosing what to open

    const lower = (s) => s.toLowerCase();
    const baseName = (p) => p.slice(p.lastIndexOf('/') + 1);
    const dirName = (p) => p.lastIndexOf('/') < 0 ? '' : p.slice(0, p.lastIndexOf('/'));
    const stripExt = (n) => n.replace(/\.(dll|exe)$/i, '').replace(/\.deps\.json$/i, '');
    const isAssembly = (p) => /\.(dll|exe)$/i.test(p);
    // dependency manifests the scan understands; a published app's .deps.json is preferred (it is what actually ships)
    const manifestRank = (p) => /\.deps\.json$/i.test(p) ? 0 : /(^|\/)project\.assets\.json$/i.test(p) ? 1 : /(^|\/)packages\.lock\.json$/i.test(p) ? 2 : -1;
    const isDeps = (p) => manifestRank(p) >= 0;

    // Assemblies that ship with the runtime. A published app folder contains copies of them; they are not what the user opened.
    const FRAMEWORK = /^(System(\.|$)|Microsoft\.(CSharp|VisualBasic|Win32|NET|Extensions|AspNetCore)|mscorlib|netstandard|WindowsBase|PresentationCore|PresentationFramework|api-ms-|clr|coreclr|hostfxr|hostpolicy|mscordaccore|mscordbi|ucrtbase|createdump|libSystem)/i;

    /** Higher = preferred when a package offers several lib/<tfm>/ folders. */
    function tfmRank(tfm) {
        const t = lower(tfm);
        let m;
        if ((m = t.match(/^net(\d+)\.(\d+)/))) return 5000 + parseInt(m[1], 10) * 10 + parseInt(m[2], 10);
        if ((m = t.match(/^netcoreapp(\d+)\.(\d+)/))) return 3000 + parseInt(m[1], 10) * 10 + parseInt(m[2], 10);
        if ((m = t.match(/^netstandard(\d+)\.(\d+)/))) return 2000 + parseInt(m[1], 10) * 10 + parseInt(m[2], 10);
        if ((m = t.match(/^net(\d)(\d)(\d?)/))) return 1000 + parseInt(m[1] + m[2] + (m[3] || '0'), 10) / 10;     // net48 = .NET Framework
        return 0;
    }

    /**
     * entries: [{ path, size }]  (paths use / or \)
     * opts.max: most assemblies to load.
     * Returns { assemblies: [path...] (the main one is LAST so it ends up active), skipped: [{path, reason}],
     *           deps: { assemblyPath: depsPath }, kind: 'package' | 'folder' | 'files' }
     */
    function plan(entries, opts) {
        const max = (opts && opts.max) || 8;
        const all = entries.map(e => ({ ...e, path: e.path.replace(/\\/g, '/').replace(/^\.?\//, '') }));
        const skipped = [];

        const isPackage = all.some(e => /^lib\//i.test(e.path)) && all.some(e => /\.nuspec$/i.test(e.path));
        const depsFiles = all.filter(e => isDeps(e.path) && !/^runtimes\//i.test(e.path));
        let candidates = all.filter(e => isAssembly(e.path));

        const drop = (list, why) => list.forEach(e => skipped.push({ path: e.path, reason: why }));

        candidates = candidates.filter(e => {
            const p = e.path;
            if (/(^|\/)(ref|runtimes|analyzers|build|buildTransitive|tools)\//i.test(p) && !/^tools\/.*\.exe$/i.test(p)) { drop([e], 'reference / native / build-time assembly'); return false; }
            if (/\.resources\.dll$/i.test(p)) { drop([e], 'resource assembly'); return false; }
            return true;
        });

        if (isPackage) {
            // keep the best target framework only: lib/net8.0 beats lib/netstandard2.0 beats lib/net48
            const libs = candidates.filter(e => /^lib\//i.test(e.path));
            const folders = [...new Set(libs.map(e => e.path.split('/')[1]))];
            const best = folders.sort((a, b) => tfmRank(b) - tfmRank(a))[0];
            candidates = candidates.filter(e => {
                if (!/^lib\//i.test(e.path)) { drop([e], 'not in lib/'); return false; }
                if (e.path.split('/')[1] !== best) { drop([e], `other target framework (${e.path.split('/')[1]}; using ${best})`); return false; }
                return true;
            });
        } else {
            const own = candidates.filter(e => !FRAMEWORK.test(baseName(e.path)));
            if (own.length > 0) {
                candidates.filter(e => FRAMEWORK.test(baseName(e.path))).forEach(e => skipped.push({ path: e.path, reason: 'framework assembly (supplied by the runtime)' }));
                candidates = own;
            }
        }

        // main assembly = the one a deps.json is named after, else an .exe, else the first
        const depsBases = new Set(depsFiles.map(d => lower(stripExt(baseName(d.path)))));
        const rank = (e) => (depsBases.has(lower(stripExt(baseName(e.path)))) ? 0 : /\.exe$/i.test(e.path) ? 1 : 2);
        candidates.sort((a, b) => rank(a) - rank(b) || dirName(a.path).length - dirName(b.path).length || a.path.localeCompare(b.path));

        const chosen = candidates.slice(0, max);
        drop(candidates.slice(max), `limit of ${max} assemblies open at once`);

        const deps = {};
        for (const a of chosen) {
            const aName = lower(stripExt(baseName(a.path)));
            // best manifest for this assembly: named after it, then in its folder, then the only one; ties go to deps.json > project.assets.json > packages.lock.json
            const score = (d) => (lower(stripExt(baseName(d.path))) === aName ? 0 : dirName(d.path) === dirName(a.path) ? 10 : depsFiles.length === 1 ? 20 : 99) + manifestRank(d.path);
            const best = depsFiles.map(d => ({ d, sc: score(d) })).filter(x => x.sc < 99).sort((x, y) => x.sc - y.sc)[0];
            if (best) deps[a.path] = best.d.path;
        }

        const ordered = chosen.map(e => e.path);
        if (ordered.length > 1) ordered.push(ordered.shift());         // the main one last, so it is the active one
        return { assemblies: ordered, skipped, deps, kind: isPackage ? 'package' : (all.length > 1 ? 'folder' : 'files') };
    }

    return { readZipEntries, plan, tfmRank, MAX_ENTRY_BYTES };
});
