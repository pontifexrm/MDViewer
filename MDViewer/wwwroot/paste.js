// Smart paste for the markdown editor.
//
// Copying from Word, Outlook, Google Docs or a web page puts an HTML version of the
// selection on the clipboard beside the plain text. A paste into the editor is
// intercepted, that HTML converted to markdown in .NET (Services/HtmlToMarkdown.cs),
// and the result inserted where the text would have gone. When the HTML has nothing
// worth converting, .NET says so and the plain text goes in as it always did.
// Ctrl+Shift+V skips all of this.
window.mdvPaste = {

    attach: function (viewer) {
        window.__mdvPasteViewer = viewer;
        if (window.__mdvPasteBound) return;
        window.__mdvPasteBound = true;

        // The paste event cannot tell Ctrl+V from Ctrl+Shift+V, so the keystroke
        // that precedes it is remembered. Any other key clears it.
        var plain = false;
        document.addEventListener('keydown', function (e) {
            plain = e.ctrlKey && e.shiftKey && (e.key === 'V' || e.key === 'v');
        }, true);

        document.addEventListener('paste', function (e) {
            var box = e.target;
            if (!box || box.tagName !== 'TEXTAREA' || !box.classList.contains('md-source')) return;

            if (plain) {
                plain = false;
                return;
            }

            var data = e.clipboardData;
            var html = data ? data.getData('text/html') : '';
            var ref = window.__mdvPasteViewer;
            if (!html || !ref) return;

            // Taken now: the conversion is asynchronous, and the default paste has to
            // be cancelled before this handler returns.
            var text = data.getData('text/plain');
            var start = box.selectionStart;
            var end = box.selectionEnd;
            e.preventDefault();

            ref.invokeMethodAsync('ConvertPaste', html).then(function (markdown) {
                if (markdown === null) insert(box, start, end, text);
                else insert(box, start, end, spaced(box.value, start, end, markdown));
            }, function () {
                insert(box, start, end, text);
            });
        });

        function insert(box, start, end, text) {
            box.focus();
            box.setSelectionRange(start, end);

            // execCommand rather than assigning the value: it goes through the
            // editing pipeline, so the paste is one Ctrl+Z step, and it fires the
            // input event Blazor binds the source to.
            if (!document.execCommand('insertText', false, text)) {
                box.setRangeText(text, start, end, 'end');
                box.dispatchEvent(new Event('input', { bubbles: true }));
            }
        }

        // A heading, list or table pasted mid-line is not a heading, list or table,
        // so block content gets blank lines either side of it.
        function spaced(value, start, end, markdown) {
            var block = markdown.indexOf('\n') >= 0 || /^(#{1,6} |- |\d+\. |> |\||```)/.test(markdown);
            if (!block) return markdown;

            var before = value.slice(0, start);
            var after = value.slice(end);

            var lead = before === '' || /\n\n$/.test(before) ? '' : /\n$/.test(before) ? '\n' : '\n\n';
            var trail = after === '' || /^\n\n/.test(after) ? '' : /^\n/.test(after) ? '\n' : '\n\n';
            return lead + markdown + trail;
        }
    }
};
