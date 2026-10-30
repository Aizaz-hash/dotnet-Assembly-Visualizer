const test = require('node:test');
const assert = require('node:assert/strict');
const { highlight } = require('../../wwwroot/js/csharp-highlight.js');

const text = (html) => html.replace(/<[^>]+>/g, '').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&amp;/g, '&');

test('keywords and literals are marked, identifiers that merely contain a keyword are not', () => {
    const html = highlight('public int internalValue = null;');
    assert.ok(html.includes('<span class="cs-kw">public</span>'));
    assert.ok(html.includes('<span class="cs-kw">int</span>'));
    assert.ok(html.includes('<span class="cs-lit">null</span>'));
    assert.ok(!html.includes('cs-kw">internal<'));
    assert.ok(html.includes('internalValue'));
});

test('comments and strings hide the keywords inside them', () => {
    const html = highlight('// public class\nvar s = "if (x) // not a comment";\n/* while */');
    assert.equal((html.match(/cs-comment/g) || []).length, 2);
    assert.equal((html.match(/cs-string/g) || []).length, 1);
    assert.ok(!html.includes('cs-kw">if<') && !html.includes('cs-kw">while<'));
});

test('escaped quotes, verbatim and interpolated strings stay in one token', () => {
    assert.equal((highlight('"a \\" b"').match(/cs-string/g) || []).length, 1);
    assert.equal((highlight('@"a ""quoted"" b"').match(/cs-string/g) || []).length, 1);
    assert.equal((highlight('$"x {y} z"').match(/cs-string/g) || []).length, 1);
    assert.equal((highlight("'\\''").match(/cs-string/g) || []).length, 1);
});

test('the text is never changed or lost, and HTML in the source is escaped', () => {
    const src = 'if (a < b && c > "<script>alert(1)</script>") { x = 1 & 2; } // </div>';
    const html = highlight(src);
    assert.equal(text(html), src);
    assert.ok(!html.includes('<script>'));
    assert.ok(!html.includes('</div>'));
});

test('numbers and preprocessor lines are marked', () => {
    const html = highlight('#region Test\nint x = 0x1F + 3.5f;\n#endregion');
    assert.equal((html.match(/cs-pre/g) || []).length, 2);
    assert.ok(html.includes('cs-number">0x1F<') && html.includes('cs-number">3.5f<'));
});

test('unterminated strings, comments and chars do not hang or lose text', () => {
    for (const src of ['"never closed', '/* never closed', "'x", '@"verbatim never closed', '$"', 'x = "line one\nline two";']) {
        assert.equal(text(highlight(src)), src);
    }
});

test('400 KB of source is highlighted quickly', () => {
    const src = 'public void Method(int a) { if (a > 0) { return "x"; } }\n'.repeat(7000);
    const t = process.hrtime.bigint();
    const html = highlight(src);
    const ms = Number(process.hrtime.bigint() - t) / 1e6;
    assert.ok(src.length > 390000);
    assert.equal(text(html).length, src.length);
    assert.ok(ms < 1500, `took ${ms.toFixed(0)} ms`);
});

test('empty input gives empty output', () => assert.equal(highlight(''), ''));
