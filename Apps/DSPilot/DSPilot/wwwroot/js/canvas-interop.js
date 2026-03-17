// Canvas drag & drop interop for DSPilot Editor (grid-cell based)
window.canvasInterop = {
    _dotNetRef: null,
    _svgEl: null,
    _dragState: null,
    _cellW: 200,
    _cellH: 200,
    _cols: 6,
    _rows: 4,

    init: function (dotNetRef, svgId, cellW, cellH, cols, rows) {
        this._dotNetRef = dotNetRef;
        this._svgEl = document.getElementById(svgId);
        this._cellW = cellW || 200;
        this._cellH = cellH || 200;
        this._cols = cols || 6;
        this._rows = rows || 4;

        if (!this._svgEl) return;

        this._onMouseDownBound = this._onMouseDown.bind(this);
        this._onMouseMoveBound = this._onMouseMove.bind(this);
        this._onMouseUpBound = this._onMouseUp.bind(this);

        this._svgEl.addEventListener('mousedown', this._onMouseDownBound);
        this._svgEl.addEventListener('mousemove', this._onMouseMoveBound);
        this._svgEl.addEventListener('mouseup', this._onMouseUpBound);
        this._svgEl.addEventListener('mouseleave', this._onMouseUpBound);
    },

    updateGrid: function (cellW, cellH, cols, rows) {
        this._cellW = cellW || 200;
        this._cellH = cellH || 200;
        this._cols = cols || 6;
        this._rows = rows || 4;
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
        var rect = this._svgEl.getBoundingClientRect();
        return {
            x: evt.clientX - rect.left,
            y: evt.clientY - rect.top
        };
    },

    _pixelToCell: function (px, py) {
        var col = Math.floor(px / this._cellW);
        var row = Math.floor(py / this._cellH);
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
