// Link handling and scrolling for the ePub reader.
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
    },

    // Called after a chapter renders: jump to the linked element, or to the top of
    // the chapter when there isn't one. Scrolling the pane rather than the window,
    // since .doc-pane is what actually overflows.
    reveal: function (fragment) {
        var pane = document.querySelector('.doc-pane');

        if (fragment) {
            var target = document.getElementById(fragment);
            if (target) {
                target.scrollIntoView({ block: 'start' });
                return;
            }
        }

        if (pane) pane.scrollTop = 0;
    }
};
