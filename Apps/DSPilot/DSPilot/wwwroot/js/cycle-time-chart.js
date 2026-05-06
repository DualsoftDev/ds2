// Cycle Time Analysis 차트 — bar 클릭 이벤트 위임
// Blazor RenderTreeBuilder 로 bar 마다 EventCallback 을 생성하면 2000+ 개 호출 시
// 매 렌더마다 callback 할당 + diff 비용이 폭증한다. SVG 는 MarkupString 으로 한 번에 렌더하고
// 클릭은 컨테이너에서 한 번만 위임 처리한다.
window.cycleTimeChart = {
    _instances: new Map(),

    init: function (container, dotnetRef) {
        if (!container) return;
        this.dispose(container);

        var handler = function (e) {
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

        container.addEventListener('click', handler);
        this._instances.set(container, handler);
    },

    dispose: function (container) {
        if (!container) return;
        var handler = this._instances.get(container);
        if (handler) {
            container.removeEventListener('click', handler);
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
