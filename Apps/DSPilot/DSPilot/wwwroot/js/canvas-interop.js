// DSPilot Canvas Interop — drag/drop, resize, delete, file download for Editor SVG
window.canvasInterop = (function () {
    var _dotNetRef = null;
    var _svg = null;
    var _cellW = 200, _cellH = 200;
    var _cols = 6, _rows = 4;
    var _ox = 0, _oy = 0;

    // ── SVG coordinate helper ──
    function svgPoint(evt) {
        var pt = _svg.createSVGPoint();
        pt.x = evt.clientX;
        pt.y = evt.clientY;
        return pt.matrixTransform(_svg.getScreenCTM().inverse());
    }

    function colRowFromSvg(sx, sy) {
        var col = Math.floor((sx - _ox) / _cellW);
        var row = Math.floor((sy - _oy) / _cellH);
        return { col: Math.max(0, Math.min(col, _cols - 1)), row: Math.max(0, Math.min(row, _rows - 1)) };
    }

    // ── Drag state ──
    var _dragging = null; // { g, flowId, startCol, startRow, offsetX, offsetY }
    var _resizing = null; // { g, flowId, startColSpan, startRowSpan, startX }

    function onPointerDown(e) {
        if (!_svg) return;

        // Delete button
        var del = e.target.closest('[data-delete-for]');
        if (del) {
            e.preventDefault();
            var fid = del.getAttribute('data-delete-for');
            if (_dotNetRef) _dotNetRef.invokeMethodAsync('OnNodeDeleted', fid);
            return;
        }

        // Resize handle
        var rh = e.target.closest('[data-resize-for]');
        if (rh) {
            e.preventDefault();
            var g = rh.closest('[data-flow-id]');
            if (!g) return;
            _resizing = {
                g: g,
                flowId: g.getAttribute('data-flow-id'),
                startColSpan: parseInt(g.getAttribute('data-colspan')) || 1,
                startRowSpan: parseInt(g.getAttribute('data-rowspan')) || 1,
                startX: e.clientX,
                startY: e.clientY
            };
            _svg.setPointerCapture(e.pointerId);
            return;
        }

        // Node drag
        var g = e.target.closest('[data-flow-id]');
        if (g) {
            e.preventDefault();
            var sp = svgPoint(e);
            _dragging = {
                g: g,
                flowId: g.getAttribute('data-flow-id'),
                startCol: parseInt(g.getAttribute('data-col')) || 0,
                startRow: parseInt(g.getAttribute('data-row')) || 0,
                startX: e.clientX,
                startY: e.clientY
            };
            g.style.opacity = '0.6';
            _svg.setPointerCapture(e.pointerId);
            return;
        }
    }

    function onPointerMove(e) {
        if (_resizing) {
            // do nothing visual while resizing, will apply on up
            return;
        }
        if (_dragging) {
            // visual feedback only
            return;
        }
    }

    function onPointerUp(e) {
        if (_resizing) {
            var dx = e.clientX - _resizing.startX;
            var dy = e.clientY - _resizing.startY;
            // Convert pixel delta to col/row delta using the SVG's screen scale
            var rect = _svg.getBoundingClientRect();
            var vb = _svg.viewBox.baseVal;
            var scaleX = vb.width / rect.width;
            var scaleY = vb.height / rect.height;
            var dCols = Math.round((dx * scaleX) / _cellW);
            var dRows = Math.round((dy * scaleY) / _cellH);
            var newCS = Math.max(1, _resizing.startColSpan + dCols);
            var newRS = Math.max(1, _resizing.startRowSpan + dRows);
            if (_dotNetRef) {
                _dotNetRef.invokeMethodAsync('OnNodeResized', _resizing.flowId, newCS, newRS);
            }
            _resizing = null;
            return;
        }
        if (_dragging) {
            _dragging.g.style.opacity = '';
            var dx = e.clientX - _dragging.startX;
            var dy = e.clientY - _dragging.startY;
            var rect = _svg.getBoundingClientRect();
            var vb = _svg.viewBox.baseVal;
            var scaleX = vb.width / rect.width;
            var scaleY = vb.height / rect.height;
            var dCols = Math.round((dx * scaleX) / _cellW);
            var dRows = Math.round((dy * scaleY) / _cellH);
            var newCol = Math.max(0, _dragging.startCol + dCols);
            var newRow = Math.max(0, _dragging.startRow + dRows);
            if (dCols !== 0 || dRows !== 0) {
                if (_dotNetRef) {
                    _dotNetRef.invokeMethodAsync('OnNodeDropped', _dragging.flowId, newCol, newRow);
                }
            }
            _dragging = null;
            return;
        }
    }

    // ── External drop from sidebar ──
    function onDragOver(e) {
        e.preventDefault();
    }

    function onDrop(e) {
        e.preventDefault();
        var flowId = e.dataTransfer.getData('text/plain');
        if (!flowId || !_svg) return;
        var sp = svgPoint(e);
        var cr = colRowFromSvg(sp.x, sp.y);
        if (_dotNetRef) {
            _dotNetRef.invokeMethodAsync('OnFlowDropped', flowId, cr.col, cr.row);
        }
    }

    return {
        init: function (dotNetRef, svgId, cellW, cellH, cols, rows, ox, oy) {
            _dotNetRef = dotNetRef;
            _svg = document.getElementById(svgId);
            _cellW = cellW; _cellH = cellH;
            _cols = cols; _rows = rows;
            _ox = ox; _oy = oy;

            if (_svg) {
                _svg.addEventListener('pointerdown', onPointerDown);
                _svg.addEventListener('pointermove', onPointerMove);
                _svg.addEventListener('pointerup', onPointerUp);
                _svg.addEventListener('dragover', onDragOver);
                _svg.addEventListener('drop', onDrop);
            }
        },

        updateGrid: function (cellW, cellH, cols, rows, ox, oy) {
            _cellW = cellW; _cellH = cellH;
            _cols = cols; _rows = rows;
            _ox = ox; _oy = oy;
        },

        downloadFile: function (filename, mimeType, base64) {
            var byteChars = atob(base64);
            var byteArr = new Uint8Array(byteChars.length);
            for (var i = 0; i < byteChars.length; i++) {
                byteArr[i] = byteChars.charCodeAt(i);
            }
            var blob = new Blob([byteArr], { type: mimeType });
            var url = URL.createObjectURL(blob);
            var a = document.createElement('a');
            a.href = url;
            a.download = filename;
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
            URL.revokeObjectURL(url);
        },

        dispose: function () {
            if (_svg) {
                _svg.removeEventListener('pointerdown', onPointerDown);
                _svg.removeEventListener('pointermove', onPointerMove);
                _svg.removeEventListener('pointerup', onPointerUp);
                _svg.removeEventListener('dragover', onDragOver);
                _svg.removeEventListener('drop', onDrop);
            }
            _dotNetRef = null;
            _svg = null;
        }
    };
})();
