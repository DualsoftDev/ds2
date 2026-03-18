// Canvas drag & drop + resize interop for DSPilot Editor (grid-cell based)
window.canvasInterop = {
    _dotNetRef: null,
    _svgEl: null,
    _dragState: null,
    _resizeState: null,
    _resizePreview: null,
    _cellW: 200,
    _cellH: 200,
    _cols: 6,
    _rows: 4,
    _offsetX: 0,
    _offsetY: 0,

    init: function (dotNetRef, svgId, cellW, cellH, cols, rows, offsetX, offsetY) {
        this._dotNetRef = dotNetRef;
        this._svgEl = document.getElementById(svgId);
        this._cellW = cellW || 200;
        this._cellH = cellH || 200;
        this._cols  = cols  || 6;
        this._rows  = rows  || 4;
        this._offsetX = offsetX || 0;
        this._offsetY = offsetY || 0;

        if (!this._svgEl) return;

        this._onMouseDownBound = this._onMouseDown.bind(this);
        this._onMouseMoveBound = this._onMouseMove.bind(this);
        this._onMouseUpBound   = this._onMouseUp.bind(this);

        this._svgEl.addEventListener('mousedown',  this._onMouseDownBound);
        this._svgEl.addEventListener('mousemove',  this._onMouseMoveBound);
        this._svgEl.addEventListener('mouseup',    this._onMouseUpBound);
        this._svgEl.addEventListener('mouseleave', this._onMouseUpBound);
    },

    updateGrid: function (cellW, cellH, cols, rows, offsetX, offsetY) {
        this._cellW = cellW || 200;
        this._cellH = cellH || 200;
        this._cols  = cols  || 6;
        this._rows  = rows  || 4;
        this._offsetX = offsetX || 0;
        this._offsetY = offsetY || 0;
    },

    getImageSize: function (imgSelector) {
        var img = document.querySelector(imgSelector);
        if (!img) return null;
        return { width: img.naturalWidth, height: img.naturalHeight };
    },

    downloadFile: function (fileName, mimeType, base64) {
        var link = document.createElement('a');
        link.href = 'data:' + mimeType + ';base64,' + base64;
        link.download = fileName;
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
    },

    dispose: function () {
        if (this._svgEl) {
            this._svgEl.removeEventListener('mousedown',  this._onMouseDownBound);
            this._svgEl.removeEventListener('mousemove',  this._onMouseMoveBound);
            this._svgEl.removeEventListener('mouseup',    this._onMouseUpBound);
            this._svgEl.removeEventListener('mouseleave', this._onMouseUpBound);
        }
        this._removeResizePreview();
        this._dotNetRef   = null;
        this._svgEl       = null;
        this._dragState   = null;
        this._resizeState = null;
    },

    // ── helpers ──────────────────────────────────────────────

    _getSvgPoint: function (evt) {
        var pt = this._svgEl.createSVGPoint();
        pt.x = evt.clientX;
        pt.y = evt.clientY;
        return pt.matrixTransform(this._svgEl.getScreenCTM().inverse());
    },

    _pixelToCell: function (px, py) {
        var col = Math.floor((px - this._offsetX) / this._cellW);
        var row = Math.floor((py - this._offsetY) / this._cellH);
        col = Math.max(0, Math.min(col, this._cols - 1));
        row = Math.max(0, Math.min(row, this._rows - 1));
        return { col: col, row: row };
    },

    _checkOverlap: function (col, row, colSpan, rowSpan, excludeFlowId) {
        var nodes = this._svgEl.querySelectorAll('[data-flow-id]');
        for (var i = 0; i < nodes.length; i++) {
            var n = nodes[i];
            if (n.getAttribute('data-flow-id') === excludeFlowId) continue;
            var nc  = parseInt(n.getAttribute('data-col')     || '0');
            var nr  = parseInt(n.getAttribute('data-row')     || '0');
            var ncs = parseInt(n.getAttribute('data-colspan') || '1');
            var nrs = parseInt(n.getAttribute('data-rowspan') || '1');
            if (col < nc + ncs && col + colSpan > nc &&
                row < nr + nrs && row + rowSpan > nr) return true;
        }
        return false;
    },

    _updateResizePreview: function (col, row, colSpan, rowSpan) {
        if (!this._resizePreview) {
            this._resizePreview = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
            this._resizePreview.setAttribute('fill',             'rgba(10,30,90,0.07)');
            this._resizePreview.setAttribute('stroke',           '#0A1E5A');
            this._resizePreview.setAttribute('stroke-width',     '2');
            this._resizePreview.setAttribute('stroke-dasharray', '6,3');
            this._resizePreview.setAttribute('rx',               '6');
            this._resizePreview.setAttribute('pointer-events',   'none');
            this._svgEl.appendChild(this._resizePreview);
        }
        var pad = 4;
        this._resizePreview.setAttribute('x',      this._offsetX + col  * this._cellW + pad);
        this._resizePreview.setAttribute('y',      this._offsetY + row  * this._cellH + pad);
        this._resizePreview.setAttribute('width',  colSpan * this._cellW - pad * 2);
        this._resizePreview.setAttribute('height', rowSpan * this._cellH - pad * 2);
    },

    _removeResizePreview: function () {
        if (this._resizePreview) {
            this._resizePreview.remove();
            this._resizePreview = null;
        }
    },

    // ── mouse events ─────────────────────────────────────────

    _onMouseDown: function (evt) {
        // 1) Resize handle takes priority
        var resizeEl = evt.target.closest('[data-resize-for]');
        if (resizeEl) {
            evt.preventDefault();
            evt.stopPropagation();
            var flowId  = resizeEl.getAttribute('data-resize-for');
            var groupEl = this._svgEl.querySelector('[data-flow-id="' + flowId + '"]');
            if (!groupEl) return;

            this._resizeState = {
                flowId:       flowId,
                col:          parseInt(groupEl.getAttribute('data-col')     || '0'),
                row:          parseInt(groupEl.getAttribute('data-row')     || '0'),
                startColSpan: parseInt(groupEl.getAttribute('data-colspan') || '1'),
                startRowSpan: parseInt(groupEl.getAttribute('data-rowspan') || '1'),
                curColSpan:   parseInt(groupEl.getAttribute('data-colspan') || '1'),
                curRowSpan:   parseInt(groupEl.getAttribute('data-rowspan') || '1')
            };
            this._svgEl.style.cursor = 'se-resize';
            return;
        }

        // 2) Move drag
        var target = evt.target.closest('[data-flow-id]');
        if (!target) return;

        evt.preventDefault();
        var pt        = this._getSvgPoint(evt);
        var startCell = this._pixelToCell(pt.x, pt.y);

        this._dragState = {
            flowId:     target.getAttribute('data-flow-id'),
            groupEl:    target,
            startCol:   parseInt(target.getAttribute('data-col')     || '0'),
            startRow:   parseInt(target.getAttribute('data-row')     || '0'),
            colSpan:    parseInt(target.getAttribute('data-colspan') || '1'),
            rowSpan:    parseInt(target.getAttribute('data-rowspan') || '1'),
            grabCol:    startCell.col,
            grabRow:    startCell.row,
            currentCol: null,
            currentRow: null
        };
        this._svgEl.style.cursor = 'grabbing';
    },

    _onMouseMove: function (evt) {
        // Resize
        if (this._resizeState) {
            evt.preventDefault();
            var pt = this._getSvgPoint(evt);
            var rs = this._resizeState;

            var newCS = Math.max(1, Math.round((pt.x - (this._offsetX + rs.col * this._cellW)) / this._cellW));
            var newRS = Math.max(1, Math.round((pt.y - (this._offsetY + rs.row * this._cellH)) / this._cellH));
            newCS = Math.min(newCS, this._cols - rs.col);
            newRS = Math.min(newRS, this._rows - rs.row);

            if (newCS === rs.curColSpan && newRS === rs.curRowSpan) return;
            rs.curColSpan = newCS;
            rs.curRowSpan = newRS;
            this._updateResizePreview(rs.col, rs.row, newCS, newRS);
            return;
        }

        // Move drag
        if (!this._dragState) return;
        evt.preventDefault();

        var pt   = this._getSvgPoint(evt);
        var cell = this._pixelToCell(pt.x, pt.y);
        var ds   = this._dragState;

        var newCol = Math.max(0, Math.min(ds.startCol + (cell.col - ds.grabCol), this._cols - ds.colSpan));
        var newRow = Math.max(0, Math.min(ds.startRow + (cell.row - ds.grabRow), this._rows - ds.rowSpan));

        if (newCol === ds.currentCol && newRow === ds.currentRow) return;
        ds.currentCol = newCol;
        ds.currentRow = newRow;

        ds.groupEl.setAttribute('transform',
            'translate(' + (newCol - ds.startCol) * this._cellW + ',' + (newRow - ds.startRow) * this._cellH + ')');

        var rect = ds.groupEl.querySelector('rect');
        if (rect) {
            var ov = this._checkOverlap(newCol, newRow, ds.colSpan, ds.rowSpan, ds.flowId);
            rect.style.stroke      = ov ? '#e53935' : '';
            rect.style.strokeWidth = ov ? '4'       : '';
        }
    },

    _onMouseUp: function (evt) {
        // Resize up
        if (this._resizeState) {
            this._svgEl.style.cursor = '';
            this._removeResizePreview();
            var rs = this._resizeState;
            if ((rs.curColSpan !== rs.startColSpan || rs.curRowSpan !== rs.startRowSpan) && this._dotNetRef) {
                this._dotNetRef.invokeMethodAsync('OnNodeResized', rs.flowId, rs.curColSpan, rs.curRowSpan);
            }
            this._resizeState = null;
            return;
        }

        // Move drag up
        if (!this._dragState) return;
        this._svgEl.style.cursor = '';
        this._dragState.groupEl.setAttribute('transform', '');
        var rect = this._dragState.groupEl.querySelector('rect');
        if (rect) { rect.style.stroke = ''; rect.style.strokeWidth = ''; }

        var ds = this._dragState;
        if (ds.currentCol !== null && ds.currentRow !== null &&
            (ds.currentCol !== ds.startCol || ds.currentRow !== ds.startRow) && this._dotNetRef) {
            this._dotNetRef.invokeMethodAsync('OnNodeDropped', ds.flowId, ds.currentCol, ds.currentRow);
        }
        this._dragState = null;
    }
};
