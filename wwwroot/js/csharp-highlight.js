/*
 * A small C# syntax highlighter for the decompiled-source view. It only wraps tokens in <span>s after escaping the text, so
 * the output is safe to assign to innerHTML whatever the source contains. Not a parser: keywords, literals, strings,
 * comments, numbers and preprocessor lines are enough to read decompiler output.
 * Runs in the browser (window.CSharpHighlight) and under node --test.
 */
(function (root, factory) {
    if (typeof module === 'object' && module.exports) module.exports = factory();
    else root.CSharpHighlight = factory();
})(typeof self !== 'undefined' ? self : this, function () {
    'use strict';

    const KEYWORDS = new Set(('abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event ' +
        'explicit extern finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new object operator out override params ' +
        'private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw try typeof uint ulong unchecked ' +
        'unsafe ushort using virtual void volatile while var async await yield get set init record when where nameof dynamic partial required file scoped').split(' '));
    const LITERALS = new Set(['true', 'false', 'null']);

    const escapeHtml = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    const wrap = (cls, text) => '<span class="cs-' + cls + '">' + escapeHtml(text) + '</span>';
    const isIdStart = (c) => /[A-Za-z_]/.test(c);
    const isIdPart = (c) => /[A-Za-z0-9_]/.test(c);
    const isDigit = (c) => c >= '0' && c <= '9';

    function highlight(code) {
        const n = code.length;
        let i = 0, out = '', atLineStart = true;

        while (i < n) {
            const c = code[i], next = code[i + 1];

            // preprocessor / line start '#'
            if (c === '#' && atLineStart) {
                let j = i; while (j < n && code[j] !== '\n') j++;
                out += wrap('pre', code.slice(i, j)); i = j; continue;
            }
            if (c === '/' && next === '/') {
                let j = i; while (j < n && code[j] !== '\n') j++;
                out += wrap('comment', code.slice(i, j)); i = j; continue;
            }
            if (c === '/' && next === '*') {
                let j = code.indexOf('*/', i + 2);
                j = j < 0 ? n : j + 2;                                   // an unterminated comment runs to the end
                out += wrap('comment', code.slice(i, j)); i = j; continue;
            }
            // strings: "...", @"...", $"...", $@"..."
            if (c === '"' || ((c === '@' || c === '$') && (next === '"' || ((next === '@' || next === '$') && code[i + 2] === '"')))) {
                let j = i; while (code[j] !== '"') j++;
                const verbatim = code.slice(i, j).includes('@');
                j++;
                while (j < n) {
                    if (verbatim) { if (code[j] === '"') { if (code[j + 1] === '"') { j += 2; continue; } break; } j++; }
                    else { if (code[j] === '\\') { j += 2; continue; } if (code[j] === '"' || code[j] === '\n') break; j++; }
                }
                j = Math.min(n, j + (code[j] === '"' ? 1 : 0));
                out += wrap('string', code.slice(i, j)); i = j; atLineStart = false; continue;
            }
            if (c === "'") {
                let j = i + 1;
                while (j < n && code[j] !== "'" && code[j] !== '\n') { if (code[j] === '\\') j++; j++; }
                j = Math.min(n, j + (code[j] === "'" ? 1 : 0));
                out += wrap('string', code.slice(i, j)); i = j; atLineStart = false; continue;
            }
            if (isDigit(c)) {
                let j = i; while (j < n && /[0-9A-Za-z_.]/.test(code[j])) j++;
                out += wrap('number', code.slice(i, j)); i = j; atLineStart = false; continue;
            }
            if (isIdStart(c) || (c === '@' && next && isIdStart(next))) {
                let j = i + (c === '@' ? 1 : 0); while (j < n && isIdPart(code[j])) j++;
                const word = code.slice(i, j);
                if (KEYWORDS.has(word)) out += wrap('kw', word);
                else if (LITERALS.has(word)) out += wrap('lit', word);
                else out += escapeHtml(word);
                i = j; atLineStart = false; continue;
            }
            if (c === '\n') atLineStart = true; else if (c !== ' ' && c !== '\t' && c !== '\r') atLineStart = false;
            out += escapeHtml(c); i++;
        }
        return out;
    }

    return { highlight, escapeHtml };
});
