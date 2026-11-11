// Right-click format menu for the markdown editor.
//
// Right-clicking in the editor opens this menu instead of the browser's own: Cut,
// Copy and Paste first, then the markdown formats, each shown beside the syntax it
// writes. A format applies to the selection, or to the word or line under the caret
// when nothing is selected, and choosing one that is already there takes it off
// again. Shift+right-click still opens the browser's menu.
//
// Every change goes in through execCommand('insertText'), as paste.js does, so it is
// one Ctrl+Z step and fires the input event Blazor binds the source to.
//
// The text logic is pure — compute(action, value, start, end) returns one edit — so
// it can be tested without a textarea.
window.mdvFormat = (function () {

    var ITEMS = [
        { id: 'cut', label: 'Cut', key: 'Ctrl+X', needsSelection: true },
        { id: 'copy', label: 'Copy', key: 'Ctrl+C', needsSelection: true },
        { id: 'paste', label: 'Paste', key: 'Ctrl+V' },
        '-',
        { id: 'bold', label: 'Bold', syntax: '**', key: 'Ctrl+B' },
        { id: 'italic', label: 'Italic', syntax: '*', key: 'Ctrl+I' },
        { id: 'strike', label: 'Strikethrough', syntax: '~~' },
        { id: 'code', label: 'Inline code', syntax: '`' },
        { id: 'link', label: 'Link', syntax: '[ ]( )', key: 'Ctrl+K' },
        '-',
        { id: 'h1', label: 'Heading 1', syntax: '#' },
        { id: 'h2', label: 'Heading 2', syntax: '##' },
        { id: 'h3', label: 'Heading 3', syntax: '###' },
        { id: 'bullet', label: 'Bulleted list', syntax: '-' },
        { id: 'number', label: 'Numbered list', syntax: '1.' },
        { id: 'task', label: 'Task list', syntax: '- [ ]' },
        { id: 'quote', label: 'Quote', syntax: '>' },
        '-',
        { id: 'fence', label: 'Code block', syntax: '```' },
        { id: 'table', label: 'Table', syntax: '| |' },
        { id: 'rule', label: 'Horizontal rule', syntax: '---' }
    ];

    var SHORTCUTS = { b: 'bold', i: 'italic', k: 'link' };
    var INLINE = { bold: '**', italic: '*', strike: '~~', code: '`' };

    // ── Text logic ───────────────────────────────────────────────────────────
    // Each returns { from, to, text, selStart, selEnd }: replace [from, to) with
    // text, then select [selStart, selEnd).

    function compute(action, v, s, e) {
        if (INLINE[action]) return inline(v, s, e, INLINE[action]);
        switch (action) {
            case 'link': return link(v, s, e);
            case 'h1': case 'h2': case 'h3': return lines(v, s, e, heading(+action[1]));
            case 'bullet': case 'number': case 'task': return lines(v, s, e, list(action));
            case 'quote': return lines(v, s, e, quote);
            case 'fence': return fence(v, s, e);
            case 'table': return block(v, s, e, '| Column 1 | Column 2 |\n| --- | --- |\n|  |  |', 2, 10);
            case 'rule': return block(v, s, e, '---', 3, 3);
        }
        return null;
    }

    var WORD = /[\p{L}\p{N}_]/u;

    function wordAt(v, p) {
        var a = p, b = p;
        while (a > 0 && WORD.test(v[a - 1])) a--;
        while (b < v.length && WORD.test(v[b])) b++;
        return [a, b];
    }

    // With nothing selected, the word under the caret; either way, without the
    // spaces a double-click tends to take along (`** word **` is not bold).
    function target(v, s, e) {
        if (s === e) { var w = wordAt(v, s); s = w[0]; e = w[1]; }
        while (s < e && /\s/.test(v[s])) s++;
        while (e > s && /\s/.test(v[e - 1])) e--;
        return [s, e];
    }

    // Whether a run of n marker characters carries marker m. A run of three
    // asterisks is both bold and italic; a run of two is bold and not italic.
    function carries(n, m) {
        return m === '*' ? n === 1 || n === 3 : n >= m.length;
    }

    function runFrom(str, i, step, c) {
        var n = 0;
        while (i >= 0 && i < str.length && str[i] === c) { n++; i += step; }
        return n;
    }

    function wrapped(t, m) {
        var c = m[0];
        if (t.length <= 2 * m.length) return false;
        return carries(runFrom(t, 0, 1, c), m) && carries(runFrom(t, t.length - 1, -1, c), m);
    }

    function inline(v, s, e, m) {
        var r = target(v, s, e);
        s = r[0]; e = r[1];
        var t = v.slice(s, e);
        var L = m.length;

        if (s === e)
            return { from: s, to: s, text: m + m, selStart: s + L, selEnd: s + L };

        // Across lines, each line separately: a marker cannot span a paragraph break.
        if (t.indexOf('\n') >= 0) {
            var parts = t.split('\n');
            var all = parts.every(function (p) { return !p.trim() || wrapped(p.trim(), m); });
            var out = parts.map(function (p) {
                var body = p.trim();
                if (!body) return p;
                var lead = p.slice(0, p.indexOf(body));
                var tail = p.slice(lead.length + body.length);
                return lead + (all ? body.slice(L, body.length - L) : m + body + m) + tail;
            }).join('\n');
            return { from: s, to: e, text: out, selStart: s, selEnd: s + out.length };
        }

        // Markers selected along with the text.
        if (wrapped(t, m)) {
            var inner = t.slice(L, t.length - L);
            return { from: s, to: e, text: inner, selStart: s, selEnd: s + inner.length };
        }

        // Markers just outside the selection.
        if (s >= L && carries(runFrom(v, s - 1, -1, m[0]), m) && carries(runFrom(v, e, 1, m[0]), m)
            && v.slice(s - L, s) === m && v.slice(e, e + L) === m)
            return { from: s - L, to: e + L, text: t, selStart: s - L, selEnd: e - L };

        return { from: s, to: e, text: m + t + m, selStart: s + L, selEnd: e + L };
    }

    function link(v, s, e) {
        var r = target(v, s, e);
        s = r[0]; e = r[1];
        var t = v.slice(s, e);

        var whole = /^\[([^\]\n]*)\]\([^)\n]*\)$/.exec(t);
        if (whole)
            return { from: s, to: e, text: whole[1], selStart: s, selEnd: s + whole[1].length };

        var after = /^\]\([^)\n]*\)/.exec(v.slice(e));
        if (s > 0 && v[s - 1] === '[' && after)
            return { from: s - 1, to: e + after[0].length, text: t, selStart: s - 1, selEnd: s - 1 + t.length };

        if (!t || /^(https?:\/\/|www\.|mailto:)\S+$/i.test(t)) {
            // No text, or the text is the address: the link text is what's missing.
            var url = t || 'url';
            return { from: s, to: e, text: '[link text](' + url + ')', selStart: s + 1, selEnd: s + 10 };
        }

        var md = '[' + t + '](url)';
        return { from: s, to: e, text: md, selStart: s + t.length + 3, selEnd: s + t.length + 6 };
    }

    // ── Line formats ─────────────────────────────────────────────────────────

    // The whole lines the selection touches. A selection that ends at the very
    // start of a line, as a triple-click or Shift+Down leaves it, stops short of it.
    function lineSpan(v, s, e) {
        var a = v.lastIndexOf('\n', s - 1) + 1;
        var last = e > s && v[e - 1] === '\n' ? e - 1 : e;
        var b = v.indexOf('\n', last);
        return [a, b < 0 ? v.length : b];
    }

    // fmt(lines) returns the new lines; the change is to be made to all of them or
    // taken off all of them, decided by whether every non-blank line already has it.
    function lines(v, s, e, fmt) {
        var span = lineSpan(v, s, e);
        var a = span[0], b = span[1];
        var text = fmt(v.slice(a, b).split('\n')).join('\n');

        if (s === e) {
            // A caret keeps its place in the text, measured from the line's end.
            var caret = Math.max(a, Math.min(a + text.length, a + text.length - (b - s)));
            return { from: a, to: b, text: text, selStart: caret, selEnd: caret };
        }
        return { from: a, to: b, text: text, selStart: a, selEnd: a + text.length };
    }

    function blank(l) { return !l.trim(); }

    var HEADING = /^ {0,3}(#{1,6})(?:[ \t]+|$)(.*)$/;

    function heading(n) {
        return function (ls) {
            var on = ls.every(function (l) {
                if (blank(l)) return true;
                var h = HEADING.exec(l);
                return h && h[1].length === n;
            });
            return ls.map(function (l) {
                if (blank(l)) return l;
                var h = HEADING.exec(l);
                var body = h ? h[2] : l.replace(/^\s+/, '');
                return on ? body : '#'.repeat(n) + ' ' + body;
            });
        };
    }

    var LIST = /^(\s*)(?:([-*+][ \t]+\[[ xX]\])[ \t]+|([-*+])[ \t]+|(\d+[.)])[ \t]+)?(.*)$/;

    function kind(l) {
        var m = LIST.exec(l);
        return m[2] ? 'task' : m[3] ? 'bullet' : m[4] ? 'number' : null;
    }

    function list(type) {
        return function (ls) {
            var on = ls.every(function (l) { return blank(l) || kind(l) === type; });
            var n = 1;
            return ls.map(function (l) {
                if (blank(l)) return l;
                var m = LIST.exec(l);
                var indent = m[1], body = m[5];
                if (on) return indent + body;
                var mark = type === 'bullet' ? '- ' : type === 'task' ? '- [ ] ' : (n++) + '. ';
                return indent + mark + body;
            });
        };
    }

    function quote(ls) {
        var on = ls.every(function (l) { return blank(l) || /^\s*>/.test(l); });
        return ls.map(function (l) {
            if (on) return l.replace(/^(\s*)> ?/, '$1');
            return blank(l) ? '>' : '> ' + l;
        });
    }

    // ── Blocks ───────────────────────────────────────────────────────────────

    // A block written mid-paragraph is not a block (`---` under a line of text even
    // makes that line a heading), so it gets a blank line either side.
    function lead(before) {
        return before === '' || /\n\n$/.test(before) ? '' : /\n$/.test(before) ? '\n' : '\n\n';
    }

    function trail(after) {
        return after === '' || /^\n\n/.test(after) ? '' : /^\n/.test(after) ? '\n' : '\n\n';
    }

    // Inserted at the caret when its line is empty, otherwise after the line, so
    // nothing already written is split or replaced. [selFrom, selTo) is the part of
    // the block to select afterwards.
    function block(v, s, e, md, selFrom, selTo) {
        var span = lineSpan(v, s, e);
        var p = s === e && span[0] === span[1] ? s : span[1];
        var before = lead(v.slice(0, p));
        var text = before + md + trail(v.slice(p));
        return { from: p, to: p, text: text, selStart: p + before.length + selFrom, selEnd: p + before.length + selTo };
    }

    var FENCE = /^\s*(```|~~~)/;

    function fence(v, s, e) {
        var span = lineSpan(v, s, e);
        var a = span[0], b = span[1];
        var ls = v.slice(a, b).split('\n');

        // On an empty line, an empty block with the caret inside it.
        if (s === e && blank(ls[0])) return block(v, s, e, '```\n\n```', 4, 4);

        if (ls.length >= 2 && FENCE.test(ls[0]) && FENCE.test(ls[ls.length - 1])) {
            var inner = ls.slice(1, -1).join('\n');
            return { from: a, to: b, text: inner, selStart: a, selEnd: a + inner.length };
        }
        return wrapFence(v, a, b, ls);
    }

    function wrapFence(v, a, b, ls) {
        var before = lead(v.slice(0, a));
        var body = ls.join('\n');
        var text = before + '```\n' + body + '\n```' + trail(v.slice(b));
        var start = a + before.length + 4;
        return { from: a, to: b, text: text, selStart: start, selEnd: start + body.length };
    }

    // ── Applying ─────────────────────────────────────────────────────────────

    function apply(box, ed) {
        box.focus();
        if (ed.from !== ed.to || ed.text) {
            box.setSelectionRange(ed.from, ed.to);
            var ok = ed.text
                ? document.execCommand('insertText', false, ed.text)
                : document.execCommand('delete');
            if (!ok) {
                box.setRangeText(ed.text, ed.from, ed.to, 'end');
                box.dispatchEvent(new Event('input', { bubbles: true }));
            }
        }
        box.setSelectionRange(ed.selStart, ed.selEnd);
    }

    function run(box, action) {
        if (action === 'cut' || action === 'copy') {
            box.focus();
            document.execCommand(action);
            return;
        }
        if (action === 'paste') return paste(box);

        var ed = compute(action, box.value, box.selectionStart, box.selectionEnd);
        if (ed) apply(box, ed);
    }

    // A menu click brings no paste event, so the clipboard is read directly — HTML
    // as well as text where it can be, so the smart paste in paste.js still applies.
    function paste(box) {
        var s = box.selectionStart, e = box.selectionEnd;
        var cb = navigator.clipboard;
        if (!cb) return blocked(box);

        var rich = cb.read
            ? cb.read().then(function (items) {
                var item = items[0];
                if (!item) return { html: '', text: '' };
                function get(type) {
                    return item.types.indexOf(type) >= 0
                        ? item.getType(type).then(function (blob) { return blob.text(); })
                        : Promise.resolve('');
                }
                return Promise.all([get('text/html'), get('text/plain')])
                    .then(function (r) { return { html: r[0], text: r[1] }; });
            })
            : Promise.reject();

        rich.catch(function () {
            return cb.readText().then(function (t) { return { html: '', text: t }; });
        }).then(function (c) {
            if (window.mdvPaste) window.mdvPaste.into(box, s, e, c.html, c.text);
        }, function () {
            blocked(box);
        });
    }

    function blocked(box) {
        box.focus();
        toast('This window won’t let the menu read the clipboard. Use Ctrl+V to paste.');
    }

    // ── Menu ─────────────────────────────────────────────────────────────────

    var menu = null, buttons = [], hot = -1, owner = null, toastTimer = 0;

    function isEditor(el) {
        return el && el.tagName === 'TEXTAREA' && el.classList.contains('md-source');
    }

    function open(box, x, y) {
        close();
        owner = box;
        var hasSelection = box.selectionStart !== box.selectionEnd;

        menu = document.createElement('div');
        menu.className = 'mdv-menu no-print';
        menu.setAttribute('role', 'menu');
        buttons = [];

        ITEMS.forEach(function (it) {
            if (it === '-') {
                var sep = document.createElement('div');
                sep.className = 'mdv-sep';
                menu.appendChild(sep);
                return;
            }
            var b = document.createElement('button');
            b.type = 'button';
            b.setAttribute('role', 'menuitem');
            b.tabIndex = -1;
            b.disabled = !!it.needsSelection && !hasSelection;
            b.dataset.action = it.id;

            var syn = document.createElement('span');
            syn.className = 'mdv-syntax';
            syn.textContent = it.syntax || '';
            var lab = document.createElement('span');
            lab.className = 'mdv-label';
            lab.textContent = it.label;
            var key = document.createElement('span');
            key.className = 'mdv-key';
            key.textContent = it.key || '';
            b.appendChild(syn);
            b.appendChild(lab);
            b.appendChild(key);

            b.addEventListener('click', function () { choose(it.id); });
            b.addEventListener('mousemove', function () { highlight(buttons.indexOf(b)); });
            menu.appendChild(b);
            buttons.push(b);
        });

        // The editor keeps focus, and so its selection, while the menu is used.
        menu.addEventListener('mousedown', function (e) { e.preventDefault(); });
        menu.addEventListener('contextmenu', function (e) { e.preventDefault(); });

        document.body.appendChild(menu);

        // Kept on screen: flipped above or to the left of the pointer near an edge.
        var w = menu.offsetWidth, h = menu.offsetHeight;
        var vw = window.innerWidth, vh = window.innerHeight;
        var left = x + w > vw - 4 ? Math.max(4, Math.min(x - w, vw - w - 4)) : x;
        var top = y + h > vh - 4 ? Math.max(4, Math.min(y - h, vh - h - 4)) : y;
        menu.style.left = left + 'px';
        menu.style.top = top + 'px';
    }

    function close() {
        if (!menu) return;
        menu.remove();
        menu = null;
        buttons = [];
        hot = -1;
        owner = null;
    }

    function choose(action) {
        var box = owner;
        close();
        if (box) run(box, action);
    }

    function highlight(i) {
        if (hot >= 0 && buttons[hot]) buttons[hot].classList.remove('hot');
        hot = i;
        if (hot >= 0 && buttons[hot]) buttons[hot].classList.add('hot');
    }

    function step(dir) {
        var n = buttons.length;
        for (var k = 1; k <= n; k++) {
            var i = ((hot < 0 ? (dir > 0 ? -1 : 0) : hot) + dir * k + n) % n;
            if (!buttons[i].disabled) { highlight(i); return; }
        }
    }

    function toast(message) {
        var t = document.querySelector('.mdv-toast');
        if (!t) {
            t = document.createElement('div');
            t.className = 'mdv-toast no-print';
            document.body.appendChild(t);
        }
        t.textContent = message;
        clearTimeout(toastTimer);
        toastTimer = setTimeout(function () { t.remove(); }, 3500);
    }

    // ── Wiring ───────────────────────────────────────────────────────────────

    document.addEventListener('contextmenu', function (e) {
        if (!isEditor(e.target) || e.shiftKey) return;
        e.preventDefault();

        // From the keyboard (Menu key, Shift+F10) there may be no pointer position.
        var x = e.clientX, y = e.clientY;
        if (!x && !y) {
            var r = e.target.getBoundingClientRect();
            x = r.left + 24; y = r.top + 24;
        }
        open(e.target, x, y);
    });

    document.addEventListener('keydown', function (e) {
        if (menu) {
            var k = e.key;
            if (k === 'Escape') { close(); }
            else if (k === 'ArrowDown') { step(1); }
            else if (k === 'ArrowUp') { step(-1); }
            else if (k === 'Home') { hot = -1; step(1); }
            else if (k === 'End') { hot = 0; step(-1); }
            else if (k === 'Enter' || k === ' ') { if (hot >= 0) choose(buttons[hot].dataset.action); else close(); }
            else { close(); return; }
            e.preventDefault();
            e.stopPropagation();
            return;
        }

        if (isEditor(e.target) && e.ctrlKey && !e.shiftKey && !e.altKey && !e.metaKey) {
            var action = SHORTCUTS[e.key.toLowerCase()];
            if (action) {
                e.preventDefault();
                run(e.target, action);
            }
        }
    }, true);

    document.addEventListener('mousedown', function (e) {
        if (menu && !menu.contains(e.target)) close();
    }, true);

    document.addEventListener('scroll', function (e) {
        if (menu && !menu.contains(e.target)) close();
    }, true);

    window.addEventListener('blur', close);
    window.addEventListener('resize', close);

    return { compute: compute };
})();
