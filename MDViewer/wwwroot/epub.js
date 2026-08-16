// Link handling, scrolling and reading-position tracking for the ePub reader.
//
// Chapter HTML arrives with its hrefs already stripped by EpubHtml — a real href
// would navigate the WebView away from the running Blazor app — and replaced by
// data-epub-* attributes. One delegated listener reads them and calls back into
// the Viewer component, which is what actually changes chapter or opens a browser.
window.mdvEpub = {

    attach: function (viewer) {
        window.__mdvEpubViewer = viewer;
        if (window.__mdvEpubBound) return;
        window.__mdvEpubBound = true;

        document.addEventListener('click', function (e) {
            var anchor = e.target && e.target.closest ? e.target.closest('a') : null;
            if (!anchor) return;

            var ref = window.__mdvEpubViewer;
            if (!ref) return;

            var chapter = anchor.getAttribute('data-epub-chapter');
            if (chapter !== null) {
                e.preventDefault();
                ref.invokeMethodAsync('EpubLinkClicked',
                    parseInt(chapter, 10),
                    anchor.getAttribute('data-epub-frag') || '');
                return;
            }

            var external = anchor.getAttribute('data-epub-external');
            if (external) {
                e.preventDefault();
                ref.invokeMethodAsync('EpubExternalClicked', external);
            }
        });

        // Scroll fires continuously, so the position is reported on a trailing
        // debounce. .NET writes it straight to disk from there rather than waiting
        // for the window to close, which would lose it if the app were killed.
        var timer = null;
        document.addEventListener('scroll', function (e) {
            if (!e.target || e.target.className !== 'doc-pane') return;

            clearTimeout(timer);
            timer = setTimeout(function () {
                var ref = window.__mdvEpubViewer;
                var at = window.mdvEpub.position();
                if (ref && at) ref.invokeMethodAsync('EpubScrolled', at.block, at.fraction);
            }, 600);
        }, true); // capture: scroll does not bubble
    },

    // Where the top of the viewport currently sits, as the index of a direct child
    // of .doc-render plus how far through that child. Structural rather than a
    // pixel offset, so it still means the same thing after the text reflows.
    position: function () {
        var pane = document.querySelector('.doc-pane');
        var render = document.querySelector('.doc-render');
        if (!pane || !render) return null;

        var blocks = render.children;
        var top = pane.getBoundingClientRect().top;

        for (var i = 0; i < blocks.length; i++) {
            var box = blocks[i].getBoundingClientRect();
            if (box.bottom > top + 1) {
                var into = box.height > 0 ? (top - box.top) / box.height : 0;
                return { block: i, fraction: Math.min(1, Math.max(0, into)) };
            }
        }

        return { block: Math.max(0, blocks.length - 1), fraction: 0 };
    },

    // Called after a chapter renders: to the linked element, or back to a
    // remembered block, or to the top. Scrolls the pane rather than the window,
    // since .doc-pane is what actually overflows.
    reveal: function (fragment, block, fraction) {
        var pane = document.querySelector('.doc-pane');

        if (fragment) {
            var target = document.getElementById(fragment);
            if (target) {
                target.scrollIntoView({ block: 'start' });
                return;
            }
        }

        if (pane && block !== null && block >= 0) {
            var render = document.querySelector('.doc-render');
            if (render && block < render.children.length) {
                var box = render.children[block].getBoundingClientRect();
                pane.scrollTop += (box.top - pane.getBoundingClientRect().top)
                                + (fraction || 0) * box.height;
                return;
            }
        }

        if (pane) pane.scrollTop = 0;
    }
};
