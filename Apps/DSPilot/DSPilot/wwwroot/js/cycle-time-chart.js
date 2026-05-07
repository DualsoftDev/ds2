// Cycle Time Analysis 차트 — bar 클릭 / 호버 이벤트 처리
// Blazor RenderTreeBuilder 로 bar 마다 EventCallback 을 생성하면 2000+ 개 호출 시 매 렌더마다
// 비용 폭증. SVG 는 MarkupString 으로 한 번에 렌더하고, 컨테이너에서 click/hover 를 위임 처리.
// 툴팁은 인라인 <title> 대신 메타 배열을 받아 단일 floating div 로 표시 — circuit 페이로드 절약.
window.cycleTimeChart = {
    _instances: new Map(), // container → { clickHandler, mouseOverHandler, mouseMoveHandler, mouseOutHandler, tooltips, tipEl }

    init: function (container, dotnetRef) {
        if (!container) return;
        this.dispose(container);

        var state = { tooltips: [], tipEl: null };

        var clickHandler = function (e) {
            var target = e.target.closest('[data-bar-idx]');
            if (!target) return;
            var idx = parseInt(target.getAttribute('data-bar-idx'), 10);
            if (Number.isNaN(idx)) return;
            try {
                dotnetRef.invokeMethodAsync('OnBarClicked', idx);
            } catch (err) {
                console.warn('[cycle-time-chart] OnBarClicked invoke failed', err);
            }
        };

        var ensureTip = function () {
            if (state.tipEl && document.body.contains(state.tipEl)) return state.tipEl;
            var el = document.createElement('div');
            el.className = 'cycle-bar-tooltip';
            el.style.cssText = [
                'position:fixed', 'pointer-events:none', 'z-index:10000',
                'background:rgba(30,30,30,0.95)', 'color:#fff', 'padding:6px 8px',
                'border-radius:4px', 'font-size:11px', 'line-height:1.4',
                'white-space:pre', 'max-width:360px', 'box-shadow:0 2px 6px rgba(0,0,0,0.3)',
                'display:none'
            ].join(';');
            document.body.appendChild(el);
            state.tipEl = el;
            return el;
        };

        var positionTip = function (el, clientX, clientY) {
            // 화면 우/하단 경계를 넘으면 좌/상으로 뒤집어 표시.
            var pad = 12;
            el.style.left = '0px';
            el.style.top = '0px';
            el.style.display = 'block';
            var rect = el.getBoundingClientRect();
            var x = clientX + pad;
            var y = clientY + pad;
            if (x + rect.width > window.innerWidth - 4) x = clientX - rect.width - pad;
            if (y + rect.height > window.innerHeight - 4) y = clientY - rect.height - pad;
            el.style.left = x + 'px';
            el.style.top = y + 'px';
        };

        var mouseOverHandler = function (e) {
            var target = e.target.closest('[data-bar-idx]');
            if (!target) return;
            var idx = parseInt(target.getAttribute('data-bar-idx'), 10);
            if (Number.isNaN(idx)) return;
            var text = state.tooltips[idx];
            if (!text) return;
            var el = ensureTip();
            el.textContent = text;
            positionTip(el, e.clientX, e.clientY);
        };

        var mouseMoveHandler = function (e) {
            if (!state.tipEl || state.tipEl.style.display === 'none') return;
            var target = e.target.closest('[data-bar-idx]');
            if (!target) {
                state.tipEl.style.display = 'none';
                return;
            }
            positionTip(state.tipEl, e.clientX, e.clientY);
        };

        var mouseOutHandler = function (e) {
            // 컨테이너 밖으로 빠져나간 경우만 숨김 (자식 → 자식 이동에는 반응 안 함).
            var related = e.relatedTarget;
            if (related && container.contains(related)) return;
            if (state.tipEl) state.tipEl.style.display = 'none';
        };

        container.addEventListener('click', clickHandler);
        container.addEventListener('mouseover', mouseOverHandler);
        container.addEventListener('mousemove', mouseMoveHandler);
        container.addEventListener('mouseout', mouseOutHandler);

        this._instances.set(container, {
            clickHandler: clickHandler,
            mouseOverHandler: mouseOverHandler,
            mouseMoveHandler: mouseMoveHandler,
            mouseOutHandler: mouseOutHandler,
            state: state
        });
    },

    /// 데이터/정렬 변경 시 호출 — 호버 시점에 lookup 할 툴팁 배열 갱신.
    setTooltips: function (container, tooltips) {
        if (!container) return;
        var inst = this._instances.get(container);
        if (!inst) return;
        inst.state.tooltips = Array.isArray(tooltips) ? tooltips : [];
        if (inst.state.tipEl) inst.state.tipEl.style.display = 'none';
    },

    dispose: function (container) {
        if (!container) return;
        var inst = this._instances.get(container);
        if (inst) {
            container.removeEventListener('click', inst.clickHandler);
            container.removeEventListener('mouseover', inst.mouseOverHandler);
            container.removeEventListener('mousemove', inst.mouseMoveHandler);
            container.removeEventListener('mouseout', inst.mouseOutHandler);
            if (inst.state.tipEl && inst.state.tipEl.parentNode) {
                inst.state.tipEl.parentNode.removeChild(inst.state.tipEl);
            }
            this._instances.delete(container);
        }
    },

    // base64 로 받은 바이너리를 파일로 다운로드. ExportCsv / ExportGanttExcel 의 inline eval 을 대체.
    downloadFile: function (fileName, base64, mimeType) {
        var byteChars = atob(base64);
        var bytes = new Uint8Array(byteChars.length);
        for (var i = 0; i < byteChars.length; i++) bytes[i] = byteChars.charCodeAt(i);
        var blob = new Blob([bytes], { type: mimeType });
        var url = URL.createObjectURL(blob);
        var a = document.createElement('a');
        a.href = url;
        a.download = fileName;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        URL.revokeObjectURL(url);
    }
};
