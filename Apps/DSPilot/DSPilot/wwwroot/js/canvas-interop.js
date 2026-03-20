// Canvas drag & drop + resize interop for DSPilot Editor (grid-cell based)
window.canvasInterop = {
    _dotNetRef: null,
    _svgEl: null,
    _svgContainer: null,
    _pendingDragFlowId: null,
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

        // HTML5 drag-and-drop from sidebar to canvas
        this._svgContainer = this._svgEl.parentElement;
        this._onDragStartBound = this._onHTMLDragStart.bind(this);
        this._onDragOverBound  = function (e) { e.preventDefault(); };
        this._onHTMLDropBound  = this._onHTMLDrop.bind(this);
        document.addEventListener('dragstart', this._onDragStartBound);
        this._svgContainer.addEventListener('dragover', this._onDragOverBound);
        this._svgContainer.addEventListener('drop',     this._onHTMLDropBound);
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

    convertDropCoords: function (clientX, clientY) {
        if (!this._svgEl) return [0, 0];
        var pt = this._svgEl.createSVGPoint();
        pt.x = clientX;
        pt.y = clientY;
        var svgPt = pt.matrixTransform(this._svgEl.getScreenCTM().inverse());
        return [svgPt.x, svgPt.y];
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
        document.removeEventListener('dragstart', this._onDragStartBound);
        if (this._svgContainer) {
            this._svgContainer.removeEventListener('dragover', this._onDragOverBound);
            this._svgContainer.removeEventListener('drop',     this._onHTMLDropBound);
        }
        this._removeResizePreview();
        this._dotNetRef        = null;
        this._svgEl            = null;
        this._svgContainer     = null;
        this._pendingDragFlowId = null;
        this._dragState        = null;
        this._resizeState      = null;
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

    // ── HTML5 drag-and-drop (sidebar → canvas) ─────────────

    _onHTMLDragStart: function (evt) {
        var el = evt.target.closest('[data-drag-flow-id]');
        if (!el) return;
        this._pendingDragFlowId = el.getAttribute('data-drag-flow-id');
        evt.dataTransfer.setData('text/plain', this._pendingDragFlowId);
        evt.dataTransfer.effectAllowed = 'move';
    },

    _onHTMLDrop: function (evt) {
        evt.preventDefault();
        if (!this._pendingDragFlowId || !this._dotNetRef) return;
        var flowId = this._pendingDragFlowId;
        this._pendingDragFlowId = null;

        var pt   = this._getSvgPoint(evt);
        var cell = this._pixelToCell(pt.x, pt.y);
        this._dotNetRef.invokeMethodAsync('OnFlowDropped', flowId, cell.col, cell.row);
    },

    // ── mouse events ─────────────────────────────────────────

    _onMouseDown: function (evt) {
        // 0) Delete button takes highest priority
        var deleteEl = evt.target.closest('[data-delete-for]');
        if (deleteEl) {
            evt.preventDefault();
            evt.stopPropagation();
            var flowId = deleteEl.getAttribute('data-delete-for');
            if (this._dotNetRef) {
                this._dotNetRef.invokeMethodAsync('OnNodeDeleted', flowId);
            }
            return;
        }

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

window.signalTimelineInterop = {
    _instances: new Map(),

    init: function (container, canvas) {
        if (!container || !canvas) return;

        var instance = this._instances.get(canvas);
        if (instance) return;

        var self = this;
        instance = {
            container: container,
            canvas: canvas,
            spacer: container.querySelector('.signal-timeline-spacer'),
            payload: null,
            frameHandle: 0,
            resizeObserver: null,
            onScroll: function () {
                self._scheduleRender(instance);
            }
        };

        container.addEventListener('scroll', instance.onScroll, { passive: true });

        if (window.ResizeObserver) {
            instance.resizeObserver = new ResizeObserver(function () {
                self._scheduleRender(instance);
            });
            instance.resizeObserver.observe(container);
        }

        this._instances.set(canvas, instance);
        this._scheduleRender(instance);
    },

    draw: function (container, canvas, payload) {
        if (!container || !canvas) return;

        var instance = this._instances.get(canvas);
        if (!instance) {
            this.init(container, canvas);
            instance = this._instances.get(canvas);
            if (!instance) return;
        }

        instance.payload = payload || null;
        this._scheduleRender(instance);
    },

    dispose: function (container, canvas) {
        if (!canvas) return;

        var instance = this._instances.get(canvas);
        if (!instance) return;

        if (instance.frameHandle) {
            cancelAnimationFrame(instance.frameHandle);
        }

        if (instance.resizeObserver) {
            instance.resizeObserver.disconnect();
        }

        if (instance.container && instance.onScroll) {
            instance.container.removeEventListener('scroll', instance.onScroll);
        }

        this._instances.delete(canvas);
    },

    _scheduleRender: function (instance) {
        if (!instance) return;

        var self = this;
        if (instance.frameHandle) {
            cancelAnimationFrame(instance.frameHandle);
        }

        instance.frameHandle = requestAnimationFrame(function () {
            self._render(instance);
        });
    },

    _render: function (instance) {
        instance.frameHandle = 0;

        var container = instance.container;
        var canvas = instance.canvas;
        var payload = instance.payload;
        if (!container || !canvas) return;

        var width = container.clientWidth;
        var height = container.clientHeight;
        if (width <= 0 || height <= 0) return;

        var dpr = window.devicePixelRatio || 1;
        var pixelWidth = Math.max(1, Math.floor(width * dpr));
        var pixelHeight = Math.max(1, Math.floor(height * dpr));

        if (canvas.width !== pixelWidth || canvas.height !== pixelHeight) {
            canvas.width = pixelWidth;
            canvas.height = pixelHeight;
        }

        canvas.style.width = width + 'px';
        canvas.style.height = height + 'px';

        var ctx = canvas.getContext('2d');
        if (!ctx) return;

        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        ctx.clearRect(0, 0, width, height);

        var rows = payload && Array.isArray(payload.rows) ? payload.rows : [];
        var labels = payload && Array.isArray(payload.segmentLabels) ? payload.segmentLabels : [];
        var layout = payload && payload.layout ? payload.layout : {};
        var headerHeight = layout.headerHeight || 48;
        var rowHeight = layout.rowHeight || 56;
        var bottomPadding = layout.bottomPadding || 96;
        var labelWidth = Math.min(width - 120, Math.max(220, layout.labelWidth || 320));
        var timelineX = labelWidth;
        var timelineWidth = Math.max(120, width - timelineX);
        var segmentCount = Math.max(
            labels.length,
            rows.length > 0 && Array.isArray(rows[0].segments) ? rows[0].segments.length : 0,
            1
        );
        var segmentWidth = timelineWidth / segmentCount;
        var scrollTop = container.scrollTop || 0;
        var virtualHeight = headerHeight + rows.length * rowHeight + bottomPadding;

        if (instance.spacer) {
            instance.spacer.style.height = Math.max(virtualHeight, height) + 'px';
        }

        ctx.fillStyle = '#f4f8fc';
        ctx.fillRect(0, 0, width, height);

        this._drawHeader(ctx, width, headerHeight, labelWidth, labels, segmentCount, segmentWidth, payload);

        if (rows.length === 0) {
            this._drawEmptyState(ctx, width, height, headerHeight, payload && payload.emptyMessage);
            return;
        }

        var firstVisibleRow = Math.max(0, Math.floor(Math.max(0, scrollTop - headerHeight) / rowHeight) - 1);
        var lastVisibleRow = Math.min(rows.length, Math.ceil((scrollTop + height - headerHeight) / rowHeight) + 3);

        ctx.save();
        ctx.beginPath();
        ctx.rect(0, headerHeight, width, Math.max(0, height - headerHeight));
        ctx.clip();

        this._drawGrid(ctx, timelineX, timelineWidth, headerHeight, height, segmentCount, segmentWidth);

        for (var index = firstVisibleRow; index < lastVisibleRow; index++) {
            var row = rows[index];
            var rowY = headerHeight + index * rowHeight - scrollTop;

            if (rowY + rowHeight < headerHeight || rowY > height) continue;

            this._drawRowBackground(ctx, width, labelWidth, rowY, rowHeight, index);
            this._drawRowLabel(ctx, row, labelWidth, rowY, rowHeight);
            this._drawSegments(ctx, row, timelineX, rowY, rowHeight, segmentCount, segmentWidth);
        }

        ctx.restore();
    },

    _drawHeader: function (ctx, width, headerHeight, labelWidth, labels, segmentCount, segmentWidth, payload) {
        ctx.fillStyle = '#0f172a';
        ctx.fillRect(0, 0, width, headerHeight);

        ctx.fillStyle = '#17223b';
        ctx.fillRect(0, 0, labelWidth, headerHeight);

        ctx.fillStyle = '#f8fafc';
        ctx.font = '600 13px "Noto Sans KR", sans-serif';
        ctx.fillText('Signal / Address', 16, 20);

        if (payload && payload.lastUpdate) {
            ctx.fillStyle = 'rgba(226, 232, 240, 0.9)';
            ctx.font = '500 11px "Noto Sans KR", sans-serif';
            ctx.fillText('Updated ' + payload.lastUpdate, 16, 36);
        }

        var maxTickLabels = Math.max(1, Math.floor((ctx.canvas.width / (window.devicePixelRatio || 1) - labelWidth) / 72));
        var labelStep = Math.max(1, Math.ceil(segmentCount / maxTickLabels));

        ctx.strokeStyle = 'rgba(148, 163, 184, 0.34)';
        ctx.lineWidth = 1;

        for (var i = 0; i <= segmentCount; i++) {
            var x = labelWidth + i * segmentWidth;
            ctx.beginPath();
            ctx.moveTo(x + 0.5, 0);
            ctx.lineTo(x + 0.5, headerHeight);
            ctx.stroke();
        }

        ctx.fillStyle = '#dbeafe';
        ctx.font = '500 10px "Noto Sans KR", sans-serif';
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';

        for (var tick = 0; tick < labels.length; tick += labelStep) {
            var centerX = labelWidth + tick * segmentWidth + (segmentWidth * labelStep) / 2;
            ctx.fillText(labels[tick], centerX, headerHeight / 2);
        }

        ctx.textAlign = 'start';
        ctx.textBaseline = 'alphabetic';
    },

    _drawGrid: function (ctx, timelineX, timelineWidth, headerHeight, height, segmentCount, segmentWidth) {
        ctx.strokeStyle = 'rgba(148, 163, 184, 0.18)';
        ctx.lineWidth = 1;

        for (var i = 0; i <= segmentCount; i++) {
            var x = timelineX + i * segmentWidth;
            ctx.beginPath();
            ctx.moveTo(x + 0.5, headerHeight);
            ctx.lineTo(x + 0.5, height);
            ctx.stroke();
        }

        ctx.beginPath();
        ctx.moveTo(timelineX + 0.5, headerHeight);
        ctx.lineTo(timelineX + 0.5, height);
        ctx.strokeStyle = 'rgba(15, 23, 42, 0.18)';
        ctx.stroke();
    },

    _drawRowBackground: function (ctx, width, labelWidth, rowY, rowHeight, index) {
        ctx.fillStyle = index % 2 === 0 ? 'rgba(255, 255, 255, 0.95)' : 'rgba(247, 250, 252, 0.95)';
        ctx.fillRect(0, rowY, width, rowHeight);

        ctx.fillStyle = index % 2 === 0 ? 'rgba(248, 250, 252, 0.98)' : 'rgba(241, 245, 249, 0.98)';
        ctx.fillRect(0, rowY, labelWidth, rowHeight);

        ctx.strokeStyle = 'rgba(148, 163, 184, 0.18)';
        ctx.beginPath();
        ctx.moveTo(0, rowY + rowHeight + 0.5);
        ctx.lineTo(width, rowY + rowHeight + 0.5);
        ctx.stroke();
    },

    _drawRowLabel: function (ctx, row, labelWidth, rowY, rowHeight) {
        var name = row && row.displayName ? row.displayName : '-';
        var address = row && row.address ? row.address : '-';
        var tagType = row && row.tagType === 'out-tag' ? 'OUT' : 'IN';
        var meta = address + '  [' + tagType + ']';
        var titleTop = rowY + Math.max(6, Math.round(rowHeight * 0.14));
        var metaTop = rowY + Math.max(28, Math.round(rowHeight * 0.56));

        ctx.fillStyle = '#0f172a';
        ctx.font = '600 11px "Noto Sans KR", sans-serif';
        ctx.textBaseline = 'top';
        ctx.fillText(this._truncateText(ctx, name, labelWidth - 24), 12, titleTop);

        ctx.fillStyle = '#64748b';
        ctx.font = '500 10px "Noto Sans KR", sans-serif';
        ctx.fillText(this._truncateText(ctx, meta, labelWidth - 24), 12, metaTop);
        ctx.textBaseline = 'alphabetic';
    },

    _drawSegments: function (ctx, row, timelineX, rowY, rowHeight, segmentCount, segmentWidth) {
        var segments = row && Array.isArray(row.segments) ? row.segments : [];
        var fillStyle = row && row.tagType === 'out-tag' ? '#f28c28' : '#177ddc';
        var glowStyle = row && row.tagType === 'out-tag' ? 'rgba(242, 140, 40, 0.22)' : 'rgba(23, 125, 220, 0.22)';
        var runStart = -1;

        for (var i = 0; i < segmentCount; i++) {
            var isActive = !!segments[i];
            if (isActive && runStart < 0) {
                runStart = i;
                continue;
            }

            if (!isActive && runStart >= 0) {
                this._drawSegmentRun(ctx, timelineX, rowY, rowHeight, segmentWidth, runStart, i - runStart, fillStyle, glowStyle);
                runStart = -1;
            }
        }

        if (runStart >= 0) {
            this._drawSegmentRun(ctx, timelineX, rowY, rowHeight, segmentWidth, runStart, segmentCount - runStart, fillStyle, glowStyle);
        }
    },

    _drawSegmentRun: function (ctx, timelineX, rowY, rowHeight, segmentWidth, startIndex, length, fillStyle, glowStyle) {
        var verticalPadding = Math.max(10, Math.round(rowHeight * 0.2));
        var x = timelineX + startIndex * segmentWidth + 2;
        var y = rowY + verticalPadding;
        var width = Math.max(3, segmentWidth * length - 4);
        var height = Math.max(12, rowHeight - verticalPadding * 2);
        var radius = Math.min(6, height / 2);

        ctx.fillStyle = glowStyle;
        this._roundRect(ctx, x, y, width, height, radius + 1);
        ctx.fill();

        ctx.fillStyle = fillStyle;
        this._roundRect(ctx, x, y + 1, width, height - 2, radius);
        ctx.fill();
    },

    _drawEmptyState: function (ctx, width, height, headerHeight, message) {
        ctx.fillStyle = 'rgba(255, 255, 255, 0.88)';
        ctx.fillRect(0, headerHeight, width, height - headerHeight);

        ctx.fillStyle = '#64748b';
        ctx.font = '500 14px "Noto Sans KR", sans-serif';
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        ctx.fillText(message || '표시할 신호가 없습니다.', width / 2, headerHeight + (height - headerHeight) / 2);
        ctx.textAlign = 'start';
        ctx.textBaseline = 'alphabetic';
    },

    _truncateText: function (ctx, text, maxWidth) {
        if (!text) return '';
        if (ctx.measureText(text).width <= maxWidth) return text;

        var ellipsis = '...';
        var result = text;
        while (result.length > 0 && ctx.measureText(result + ellipsis).width > maxWidth) {
            result = result.slice(0, -1);
        }

        return result + ellipsis;
    },

    _roundRect: function (ctx, x, y, width, height, radius) {
        var r = Math.min(radius, width / 2, height / 2);
        ctx.beginPath();
        ctx.moveTo(x + r, y);
        ctx.arcTo(x + width, y, x + width, y + height, r);
        ctx.arcTo(x + width, y + height, x, y + height, r);
        ctx.arcTo(x, y + height, x, y, r);
        ctx.arcTo(x, y, x + width, y, r);
        ctx.closePath();
    }
};
