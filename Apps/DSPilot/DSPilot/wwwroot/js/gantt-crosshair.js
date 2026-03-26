// Gantt Chart Crosshair & Drag-to-Select with Edge Snapping
window.ganttCrosshair = {
    _instances: new Map(),

    init: function (containerElement, params) {
        console.log('[gantt-crosshair] init called', containerElement, params ? 'params OK' : 'NO params');
        if (!containerElement) {
            console.warn('[gantt-crosshair] containerElement is null');
            return;
        }

        this.dispose(containerElement);

        var svgEl = containerElement.querySelector('svg');
        if (!svgEl) {
            console.warn('[gantt-crosshair] SVG not found in container');
            return;
        }

        var leftMargin = params.leftMargin;
        var topMargin = params.topMargin;
        var plotWidth = params.plotWidth;
        var plotHeight = params.plotHeight;
        var xScale = params.xScale;
        var chartStartTimeMs = params.chartStartTimeMs;
        var edges = (params.snapEdges || []).slice().sort(function (a, b) { return a.x - b.x; });
        var snapThreshold = 8;

        // --- Create SVG overlay elements ---
        var svgNS = 'http://www.w3.org/2000/svg';

        var overlayGroup = document.createElementNS(svgNS, 'g');
        overlayGroup.setAttribute('class', 'gantt-crosshair-overlay');
        overlayGroup.setAttribute('pointer-events', 'none');

        // Crosshair vertical line
        var crosshairLine = document.createElementNS(svgNS, 'line');
        crosshairLine.setAttribute('stroke', '#FF5722');
        crosshairLine.setAttribute('stroke-width', '1.5');
        crosshairLine.setAttribute('stroke-dasharray', '4,3');
        crosshairLine.setAttribute('opacity', '0.85');
        crosshairLine.style.display = 'none';
        overlayGroup.appendChild(crosshairLine);

        // Drag selection rectangle
        var selectionRect = document.createElementNS(svgNS, 'rect');
        selectionRect.setAttribute('fill', 'rgba(255, 87, 34, 0.12)');
        selectionRect.setAttribute('stroke', '#FF5722');
        selectionRect.setAttribute('stroke-width', '1');
        selectionRect.setAttribute('stroke-dasharray', '4,2');
        selectionRect.style.display = 'none';
        overlayGroup.appendChild(selectionRect);

        // Drag start marker line
        var dragStartLine = document.createElementNS(svgNS, 'line');
        dragStartLine.setAttribute('stroke', '#FF5722');
        dragStartLine.setAttribute('stroke-width', '1.5');
        dragStartLine.setAttribute('opacity', '0.9');
        dragStartLine.style.display = 'none';
        overlayGroup.appendChild(dragStartLine);

        svgEl.appendChild(overlayGroup);

        // --- Create HTML tooltip (inline styles to avoid CSS scoping issues) ---
        var tooltip = document.createElement('div');
        tooltip.style.cssText = 'display:none; position:absolute; pointer-events:none; z-index:1000; ' +
            'background:rgba(33,33,33,0.92); color:#fff; padding:6px 10px; border-radius:6px; ' +
            'font-size:12px; font-family:Consolas,Monaco,monospace; white-space:nowrap; ' +
            'box-shadow:0 2px 8px rgba(0,0,0,0.25); line-height:1.4;';
        containerElement.style.position = 'relative';
        containerElement.appendChild(tooltip);

        // --- State ---
        var state = {
            isDragging: false,
            dragStartX: 0,
            dragStartTimeMs: 0,
            snappedAtStart: false
        };

        // --- Helpers ---
        function formatTime(epochMs) {
            var d = new Date(epochMs);
            var h = String(d.getHours()).padStart(2, '0');
            var m = String(d.getMinutes()).padStart(2, '0');
            var s = String(d.getSeconds()).padStart(2, '0');
            var ms = String(d.getMilliseconds()).padStart(3, '0');
            return h + ':' + m + ':' + s + '.' + ms;
        }

        function formatDuration(ms) {
            var absMs = Math.abs(ms);
            if (absMs < 1000) return absMs.toFixed(1) + ' ms';
            if (absMs < 60000) return (absMs / 1000).toFixed(2) + ' s';
            return (absMs / 60000).toFixed(2) + ' min';
        }

        function xToTimeMs(svgX) {
            var offsetMs = (svgX - leftMargin) / xScale;
            return chartStartTimeMs + offsetMs;
        }

        function findNearestEdge(svgX) {
            if (edges.length === 0) return null;

            var lo = 0, hi = edges.length - 1;
            while (lo < hi) {
                var mid = (lo + hi) >> 1;
                if (edges[mid].x < svgX) lo = mid + 1;
                else hi = mid;
            }

            var best = null;
            var bestDist = snapThreshold + 1;

            for (var i = Math.max(0, lo - 1); i <= Math.min(edges.length - 1, lo + 1); i++) {
                var dist = Math.abs(edges[i].x - svgX);
                if (dist < bestDist) {
                    bestDist = dist;
                    best = edges[i];
                }
            }

            return bestDist <= snapThreshold ? best : null;
        }

        function getScale() {
            // Detect CSS transform scale on SVG or its parent wrapper
            var wrapper = svgEl.parentElement;
            if (wrapper) {
                var style = window.getComputedStyle(wrapper);
                var transform = style.transform;
                if (transform && transform !== 'none') {
                    var match = transform.match(/matrix\(([^,]+)/);
                    if (match) return parseFloat(match[1]) || 1;
                }
            }
            return 1;
        }

        function getSvgX(e) {
            var rect = svgEl.getBoundingClientRect();
            var scale = getScale();
            return (e.clientX - rect.left) / scale;
        }

        function getSvgY(e) {
            var rect = svgEl.getBoundingClientRect();
            var scale = getScale();
            return (e.clientY - rect.top) / scale;
        }

        function isInChartArea(svgX, svgY) {
            return svgX >= leftMargin && svgX <= leftMargin + plotWidth &&
                   svgY >= topMargin - 10 && svgY <= topMargin + plotHeight + 10;
        }

        function showCrosshair(svgX) {
            crosshairLine.setAttribute('x1', svgX);
            crosshairLine.setAttribute('y1', topMargin);
            crosshairLine.setAttribute('x2', svgX);
            crosshairLine.setAttribute('y2', topMargin + plotHeight);
            crosshairLine.style.display = '';
        }

        function hideCrosshair() {
            crosshairLine.style.display = 'none';
        }

        function showTooltip(e, html) {
            var containerRect = containerElement.getBoundingClientRect();
            var x = e.clientX - containerRect.left + 14;
            var y = e.clientY - containerRect.top - 10;

            tooltip.innerHTML = html;
            tooltip.style.display = 'block';
            tooltip.style.left = x + 'px';
            tooltip.style.top = y + 'px';

            // Prevent overflow to the right
            requestAnimationFrame(function () {
                var tooltipRect = tooltip.getBoundingClientRect();
                if (tooltipRect.right > containerRect.right - 4) {
                    tooltip.style.left = (x - tooltipRect.width - 28) + 'px';
                }
                if (tooltipRect.top < containerRect.top) {
                    tooltip.style.top = (y + 24) + 'px';
                }
            });
        }

        function hideTooltip() {
            tooltip.style.display = 'none';
        }

        function showSelection(x1, x2) {
            var minX = Math.max(leftMargin, Math.min(x1, x2));
            var maxX = Math.min(leftMargin + plotWidth, Math.max(x1, x2));
            selectionRect.setAttribute('x', minX);
            selectionRect.setAttribute('y', topMargin);
            selectionRect.setAttribute('width', Math.max(0, maxX - minX));
            selectionRect.setAttribute('height', plotHeight);
            selectionRect.style.display = '';
        }

        function hideSelection() {
            selectionRect.style.display = 'none';
            dragStartLine.style.display = 'none';
        }

        function showDragStartLine(svgX) {
            dragStartLine.setAttribute('x1', svgX);
            dragStartLine.setAttribute('y1', topMargin);
            dragStartLine.setAttribute('x2', svgX);
            dragStartLine.setAttribute('y2', topMargin + plotHeight);
            dragStartLine.style.display = '';
        }

        // --- Ensure overlay is on top (Blazor re-renders may reorder DOM) ---
        function ensureOverlay() {
            if (!overlayGroup.parentNode || overlayGroup.parentNode !== svgEl) {
                // SVG was replaced by Blazor re-render; re-find and re-attach
                var newSvg = containerElement.querySelector('svg');
                if (newSvg && newSvg !== svgEl) {
                    svgEl = newSvg;
                    svgEl.appendChild(overlayGroup);
                } else if (svgEl && !overlayGroup.parentNode) {
                    svgEl.appendChild(overlayGroup);
                }
            } else {
                // Ensure overlay is last child (on top)
                if (svgEl.lastChild !== overlayGroup) {
                    svgEl.appendChild(overlayGroup);
                }
            }
        }

        // --- Event handlers (on container div, not SVG, for stability across re-renders) ---
        function onMouseMove(e) {
            ensureOverlay();
            var svgX = getSvgX(e);
            var svgY = getSvgY(e);

            if (!isInChartArea(svgX, svgY) && !state.isDragging) {
                hideCrosshair();
                hideTooltip();
                return;
            }

            // Clamp to chart area
            var clampedX = Math.max(leftMargin, Math.min(leftMargin + plotWidth, svgX));

            // Snap to nearest edge
            var snap = findNearestEdge(clampedX);
            var displayX = snap ? snap.x : clampedX;
            var timeMs = snap ? snap.timeMs : xToTimeMs(clampedX);

            showCrosshair(displayX);

            if (state.isDragging) {
                showSelection(state.dragStartX, displayX);
                var deltaMs = timeMs - state.dragStartTimeMs;

                var snapLabel = snap ? ' <span style="color:#FF5722;font-size:11px;">[SNAP]</span>' : '';
                var startSnapLabel = state.snappedAtStart ? ' <span style="color:#FF5722;font-size:11px;">[SNAP]</span>' : '';

                showTooltip(e,
                    '<div style="font-weight:600;margin-bottom:3px;">\u0394 ' + formatDuration(Math.abs(deltaMs)) + '</div>' +
                    '<div style="font-size:11px;color:#aaa;">' +
                    formatTime(state.dragStartTimeMs) + startSnapLabel +
                    ' \u2192 ' + formatTime(timeMs) + snapLabel +
                    '</div>'
                );
            } else {
                var snapIndicator = snap ? ' <span style="color:#FF5722;font-weight:600;">[SNAP]</span>' : '';
                showTooltip(e,
                    '<span style="font-weight:600;">' + formatTime(timeMs) + '</span>' + snapIndicator
                );
            }
        }

        function onMouseDown(e) {
            if (e.button !== 0) return;

            ensureOverlay();
            var svgX = getSvgX(e);
            var svgY = getSvgY(e);
            if (!isInChartArea(svgX, svgY)) return;

            var clampedX = Math.max(leftMargin, Math.min(leftMargin + plotWidth, svgX));
            var snap = findNearestEdge(clampedX);
            var displayX = snap ? snap.x : clampedX;
            var timeMs = snap ? snap.timeMs : xToTimeMs(clampedX);

            state.isDragging = true;
            state.dragStartX = displayX;
            state.dragStartTimeMs = timeMs;
            state.snappedAtStart = !!snap;

            showDragStartLine(displayX);
            hideSelection();

            e.preventDefault();
        }

        function onMouseUp(e) {
            if (!state.isDragging) return;
            state.isDragging = false;
        }

        function onMouseLeave(e) {
            if (!state.isDragging) {
                hideCrosshair();
                hideTooltip();
                hideSelection();
            }
        }

        // Attach to container div (survives SVG re-renders by Blazor)
        containerElement.style.cursor = 'crosshair';
        containerElement.addEventListener('mousemove', onMouseMove);
        containerElement.addEventListener('mousedown', onMouseDown);
        containerElement.addEventListener('mouseup', onMouseUp);
        containerElement.addEventListener('mouseleave', onMouseLeave);

        this._instances.set(containerElement, {
            overlayGroup: overlayGroup,
            tooltip: tooltip,
            handlers: {
                mousemove: onMouseMove,
                mousedown: onMouseDown,
                mouseup: onMouseUp,
                mouseleave: onMouseLeave
            }
        });

        console.log('[gantt-crosshair] initialized. plotWidth=' + plotWidth + ', edges=' + edges.length);
    },

    dispose: function (containerElement) {
        if (!containerElement) return;

        var instance = this._instances.get(containerElement);
        if (!instance) return;

        containerElement.removeEventListener('mousemove', instance.handlers.mousemove);
        containerElement.removeEventListener('mousedown', instance.handlers.mousedown);
        containerElement.removeEventListener('mouseup', instance.handlers.mouseup);
        containerElement.removeEventListener('mouseleave', instance.handlers.mouseleave);
        containerElement.style.cursor = '';

        if (instance.overlayGroup && instance.overlayGroup.parentNode) {
            instance.overlayGroup.parentNode.removeChild(instance.overlayGroup);
        }

        if (instance.tooltip && instance.tooltip.parentNode) {
            instance.tooltip.parentNode.removeChild(instance.tooltip);
        }

        this._instances.delete(containerElement);
    }
};
