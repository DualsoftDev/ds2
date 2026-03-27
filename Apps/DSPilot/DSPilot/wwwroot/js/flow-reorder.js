// DSPilot Flow Reorder — pointer-based drag reorder for flow list + sidebar dragstart
window.flowReorder = (function () {
    var _dotNetRef = null;
    var _container = null;
    var _dragging = null; // { el, startY, itemH, items, currentIndex, newIndex }

    function init(dotNetRef, containerId) {
        _dotNetRef = dotNetRef;
        _container = document.getElementById(containerId);
        if (!_container) return;

        _container.addEventListener('pointerdown', onPointerDown);
        window.addEventListener('pointermove', onPointerMove);
        window.addEventListener('pointerup', onPointerUp);

        // Sidebar → Canvas drag: set flow ID in dataTransfer
        _container.addEventListener('dragstart', onDragStart);
    }

    function onDragStart(e) {
        // When HTML5 drag starts on a flow item, set the flow ID for canvas drop
        var item = e.target.closest('[data-drag-flow-id]');
        if (!item) return;
        // If reorder is in progress, block HTML5 drag
        if (_dragging) { e.preventDefault(); return; }
        e.dataTransfer.setData('text/plain', item.getAttribute('data-drag-flow-id'));
        e.dataTransfer.effectAllowed = 'move';
    }

    function onPointerDown(e) {
        var handle = e.target.closest('[data-reorder-handle]');
        if (!handle) return;

        e.preventDefault();
        e.stopPropagation();

        var item = handle.closest('.ed-flow-item');
        if (!item || !_container) return;

        var items = Array.from(_container.querySelectorAll('.ed-flow-item'));
        var idx = items.indexOf(item);
        if (idx < 0) return;

        var rect = item.getBoundingClientRect();

        _dragging = {
            el: item,
            pointerId: e.pointerId,
            startY: e.clientY,
            itemH: rect.height + 4, // include margin
            items: items,
            currentIndex: idx,
            newIndex: idx
        };

        item.classList.add('reorder-dragging');
        try { _container.setPointerCapture(e.pointerId); } catch (_) { }
    }

    function onPointerMove(e) {
        if (!_dragging) return;
        e.preventDefault();

        var dy = e.clientY - _dragging.startY;
        _dragging.el.style.transform = 'translateY(' + dy + 'px)';
        _dragging.el.style.zIndex = '100';
        _dragging.el.style.position = 'relative';

        // Determine new index based on pointer position
        var newIndex = _dragging.currentIndex;
        var items = _dragging.items;

        for (var i = 0; i < items.length; i++) {
            if (items[i] === _dragging.el) continue;
            var rect = items[i].getBoundingClientRect();
            var mid = rect.top + rect.height / 2;

            if (i < _dragging.currentIndex && e.clientY < mid) {
                newIndex = i;
                break;
            }
            if (i > _dragging.currentIndex && e.clientY > mid) {
                newIndex = i;
            }
        }

        // Visual feedback — shift other items
        for (var j = 0; j < items.length; j++) {
            if (items[j] === _dragging.el) continue;
            if (_dragging.currentIndex < newIndex) {
                // Moving down: items between old and new shift up
                items[j].style.transform = (j > _dragging.currentIndex && j <= newIndex)
                    ? 'translateY(-' + _dragging.itemH + 'px)' : '';
            } else {
                // Moving up: items between new and old shift down
                items[j].style.transform = (j >= newIndex && j < _dragging.currentIndex)
                    ? 'translateY(' + _dragging.itemH + 'px)' : '';
            }
            items[j].style.transition = 'transform 150ms ease';
        }

        _dragging.newIndex = newIndex;
    }

    function onPointerUp(e) {
        if (!_dragging) return;

        var items = _dragging.items;
        // Reset all transforms
        for (var i = 0; i < items.length; i++) {
            items[i].style.transform = '';
            items[i].style.zIndex = '';
            items[i].style.position = '';
            items[i].style.transition = '';
        }
        _dragging.el.classList.remove('reorder-dragging');

        try { _container.releasePointerCapture(_dragging.pointerId); } catch (_) { }

        if (_dragging.newIndex !== _dragging.currentIndex && _dotNetRef) {
            _dotNetRef.invokeMethodAsync('OnFlowReordered', _dragging.currentIndex, _dragging.newIndex);
        }

        _dragging = null;
    }

    function dispose() {
        if (_container) {
            _container.removeEventListener('pointerdown', onPointerDown);
            _container.removeEventListener('dragstart', onDragStart);
        }
        window.removeEventListener('pointermove', onPointerMove);
        window.removeEventListener('pointerup', onPointerUp);
        _dotNetRef = null;
        _container = null;
        _dragging = null;
    }

    return { init: init, dispose: dispose };
})();
