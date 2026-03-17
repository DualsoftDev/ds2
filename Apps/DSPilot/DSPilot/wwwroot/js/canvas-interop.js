// Canvas drag & drop interop for DSPilot Editor (grid-cell based)
window.canvasInterop = {
    _dotNetRef: null,
    _svgEl: null,
    _dragState: null,
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
        this._cols = cols || 6;
        this._rows = rows || 4;
        this._offsetX = offsetX || 0;
        this._offsetY = offsetY || 0;

        if (!this._svgEl) return;

        this._onMouseDownBound = this._onMouseDown.bind(this);
        this._onMouseMoveBound = this._onMouseMove.bind(this);
        this._onMouseUpBound = this._onMouseUp.bind(this);

        this._svgEl.addEventListener('mousedown', this._onMouseDownBound);
        this._svgEl.addEventListener('mousemove', this._onMouseMoveBound);
        this._svgEl.addEventListener('mouseup', this._onMouseUpBound);
        this._svgEl.addEventListener('mouseleave', this._onMouseUpBound);
    },

    updateGrid: function (cellW, cellH, cols, rows, offsetX, offsetY) {
        this._cellW = cellW || 200;
        this._cellH = cellH || 200;
        this._cols = cols || 6;
        this._rows = rows || 4;
        this._offsetX = offsetX || 0;
        this._offsetY = offsetY || 0;
    },

    getImageSize: function (imgSelector) {
        var img = document.querySelector(imgSelector);
        if (!img) return null;
        return { width: img.naturalWidth, height: img.naturalHeight };
    },

    dispose: function () {
        if (this._svgEl) {
            this._svgEl.removeEventListener('mousedown', this._onMouseDownBound);
            this._svgEl.removeEventListener('mousemove', this._onMouseMoveBound);
            this._svgEl.removeEventListener('mouseup', this._onMouseUpBound);
            this._svgEl.removeEventListener('mouseleave', this._onMouseUpBound);
        }
        this._dotNetRef = null;
        this._svgEl = null;
        this._dragState = null;
    },

    _getSvgPoint: function (evt) {
        // Convert screen coords to SVG viewBox coords
        var pt = this._svgEl.createSVGPoint();
        pt.x = evt.clientX;
        pt.y = evt.clientY;
        var ctm = this._svgEl.getScreenCTM().inverse();
        var svgPt = pt.matrixTransform(ctm);
        return { x: svgPt.x, y: svgPt.y };
    },

    _pixelToCell: function (px, py) {
        // Subtract offset before computing cell
        var col = Math.floor((px - this._offsetX) / this._cellW);
        var row = Math.floor((py - this._offsetY) / this._cellH);
        col = Math.max(0, Math.min(col, this._cols - 1));
        row = Math.max(0, Math.min(row, this._rows - 1));
        return { col: col, row: row };
    },

    _onMouseDown: function (evt) {
        var target = evt.target.closest('[data-flow-id]');
        if (!target) return;

        evt.preventDefault();
        var flowId = target.getAttribute('data-flow-id');
        var pt = this._getSvgPoint(evt);
        var startCell = this._pixelToCell(pt.x, pt.y);

        this._dragState = {
            flowId: flowId,
            groupEl: target,
            startCol: parseInt(target.getAttribute('data-col') || '0'),
            startRow: parseInt(target.getAttribute('data-row') || '0'),
            colSpan: parseInt(target.getAttribute('data-colspan') || '1'),
            rowSpan: parseInt(target.getAttribute('data-rowspan') || '1'),
            grabCol: startCell.col,
            grabRow: startCell.row,
            currentCol: null,
            currentRow: null
        };

        this._svgEl.style.cursor = 'grabbing';
    },

    _onMouseMove: function (evt) {
        if (!this._dragState) return;
        evt.preventDefault();

        var pt = this._getSvgPoint(evt);
        var cell = this._pixelToCell(pt.x, pt.y);

        // Compute delta in grid cells
        var dCol = cell.col - this._dragState.grabCol;
        var dRow = cell.row - this._dragState.grabRow;
        var newCol = this._dragState.startCol + dCol;
        var newRow = this._dragState.startRow + dRow;

        // Clamp to grid bounds considering span
        newCol = Math.max(0, Math.min(newCol, this._cols - this._dragState.colSpan));
        newRow = Math.max(0, Math.min(newRow, this._rows - this._dragState.rowSpan));

        if (newCol === this._dragState.currentCol && newRow === this._dragState.currentRow) return;

        this._dragState.currentCol = newCol;
        this._dragState.currentRow = newRow;

        // Visual feedback: translate the group
        var dx = (newCol - this._dragState.startCol) * this._cellW;
        var dy = (newRow - this._dragState.startRow) * this._cellH;
        this._dragState.groupEl.setAttribute('transform', 'translate(' + dx + ',' + dy + ')');
    },

    downloadFile: function (fileName, mimeType, base64) {
        var link = document.createElement('a');
        link.href = 'data:' + mimeType + ';base64,' + base64;
        link.download = fileName;
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
    },

    _onMouseUp: function (evt) {
        if (!this._dragState) return;

        this._svgEl.style.cursor = '';
        this._dragState.groupEl.setAttribute('transform', '');

        var finalCol = this._dragState.currentCol;
        var finalRow = this._dragState.currentRow;

        if (finalCol !== null && finalRow !== null &&
            (finalCol !== this._dragState.startCol || finalRow !== this._dragState.startRow)) {
            if (this._dotNetRef) {
                this._dotNetRef.invokeMethodAsync('OnNodeDropped',
                    this._dragState.flowId, finalCol, finalRow);
            }
        }

        this._dragState = null;
    }
};
