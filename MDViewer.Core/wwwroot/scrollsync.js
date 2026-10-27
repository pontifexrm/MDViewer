// Keeps the editor and the preview at the same place in the document.
//
// The preview's blocks carry data-line, the source line each one starts on
// (MarkdownRenderer.ForScreen). The editor never wraps, so a line's offset there
// is just line × line-height. Those two give matching scroll positions for every
// block; between blocks the position is interpolated, so a long paragraph or code
// block scrolls smoothly rather than jumping from one block to the next.
//
// Whichever pane the user scrolls drives the other. Setting the other pane's
// scrollTop fires a scroll event on it too, which is recognised and ignored, so
// the two never chase each other.
(function () {

    var expected = new Map();   // pane -> the scrollTop this script last gave it
    var pending = null;
    var pairs = null;           // cached; dropped whenever the layout may have moved
    var lastEditor = null;

    function editor() { return document.querySelector('textarea.md-source'); }
    function preview() { return document.querySelector('.doc-pane'); }

    document.addEventListener('scroll', function (e) {
        var ed = editor(), pv = preview(), target = e.target;
        if (!ed || !pv || (target !== ed && target !== pv)) return;

        var given = expected.get(target);
        expected.delete(target);
        if (given !== undefined && Math.abs(target.scrollTop - given) < 2) return;

        // One sync per frame, however fast the events arrive.
        var first = pending === null;
        pending = target;
        if (first) requestAnimationFrame(function () {
            var from = pending;
            pending = null;
            if (from === editor()) sync(from, preview());
            else if (from === preview()) sync(from, editor());
        });
    }, true); // capture: scroll does not bubble

    // Opening the editor, typing (which re-renders the preview) and the status bar
    // coming and going all change the DOM. A newly opened editor is brought to where
    // the reader was; otherwise the editor is what the user is working in, so the
    // preview follows it.
    new MutationObserver(function () {
        pairs = null;
        var ed = editor(), pv = preview();
        var opened = ed !== lastEditor;
        lastEditor = ed;
        if (!ed || !pv) return;

        if (opened) sync(pv, ed);
        else sync(ed, pv);
    }).observe(document.body, { childList: true, subtree: true, characterData: true });

    window.addEventListener('resize', function () { pairs = null; });

    function sync(from, to) {
        var ed = editor(), pv = preview();
        if (!ed || !pv || !document.querySelector('.doc-render')) return;

        var map = pairs || (pairs = anchors(ed, pv));
        var fromEditor = from === ed;
        var y = interpolate(map, from.scrollTop, fromEditor ? 0 : 1, fromEditor ? 1 : 0);

        y = Math.max(0, Math.min(to.scrollHeight - to.clientHeight, y));
        if (Math.abs(to.scrollTop - y) < 1) return;

        expected.set(to, y);
        to.scrollTop = y;
    }

    // [editor scrollTop, preview scrollTop] pairs, both strictly rising. A block
    // counts as lined up with its source line when it sits as far below the
    // preview's top as the editor's text sits below its own (the top padding).
    function anchors(ed, pv) {
        var style = getComputedStyle(ed);
        var lineHeight = parseFloat(style.lineHeight) || parseFloat(style.fontSize) * 1.6;
        var pad = parseFloat(style.paddingTop) || 0;

        var edMax = ed.scrollHeight - ed.clientHeight;
        var pvMax = pv.scrollHeight - pv.clientHeight;
        var origin = pv.getBoundingClientRect().top - pv.scrollTop;

        var map = [[0, 0]];
        var blocks = pv.querySelectorAll('.doc-render [data-line]');
        for (var i = 0; i < blocks.length; i++) {
            var e = parseInt(blocks[i].getAttribute('data-line'), 10) * lineHeight;
            var p = blocks[i].getBoundingClientRect().top - origin - pad;
            if (e >= edMax || p >= pvMax) break;

            // Nested blocks (a list and its first item) share a line, and hidden
            // ones have no position; only a step forward in both panes is kept.
            var last = map[map.length - 1];
            if (e > last[0] && p > last[1]) map.push([e, p]);
        }

        // The ends meet too, so the last screenful of each pane lines up.
        var end = map[map.length - 1];
        map.push([Math.max(edMax, end[0] + 1), Math.max(pvMax, end[1] + 1)]);
        return map;
    }

    function interpolate(map, value, a, b) {
        for (var i = 1; i < map.length; i++) {
            var lo = map[i - 1], hi = map[i];
            if (value <= hi[a]) {
                var t = Math.max(0, (value - lo[a]) / (hi[a] - lo[a]));
                return lo[b] + t * (hi[b] - lo[b]);
            }
        }
        return map[map.length - 1][b];
    }
})();
