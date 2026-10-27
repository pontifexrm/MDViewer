// Clickable task lists in the reading view.
//
// "- [ ] thing" renders as a checkbox. A click on one is cancelled here — the box
// does not flip itself — and the Viewer is told which box it was, counting in
// document order, and what state it showed. .NET edits the markdown and re-renders,
// so what is on screen is always what is in the file.
window.mdvTasks = {

    attach: function (viewer) {
        window.__mdvTasksViewer = viewer;
        if (window.__mdvTasksBound) return;
        window.__mdvTasksBound = true;

        document.addEventListener('click', function (e) {
            var box = e.target;
            if (!box || box.tagName !== 'INPUT' || box.type !== 'checkbox') return;

            var render = box.closest('.doc-render');
            var ref = window.__mdvTasksViewer;
            if (!render || render.classList.contains('book') || !ref) return;

            e.preventDefault();

            // preventDefault restores the box only once the handler returns, so
            // checked still reads as the toggled state here.
            var boxes = render.querySelectorAll('input[type="checkbox"]');
            var index = Array.prototype.indexOf.call(boxes, box);
            ref.invokeMethodAsync('TaskClicked', index, !box.checked);
        });
    }
};
