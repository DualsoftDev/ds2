window.initWidgetResize = function (widgetId, dotnetRef) {
    const widgetEl = document.getElementById('widget-' + widgetId);
    if (!widgetEl) return;
    const handleEl = widgetEl.querySelector('.wgt-resize-handle');
    if (!handleEl || handleEl._ri) return;
    handleEl._ri = true;

    handleEl.addEventListener('mousedown', function (e0) {
        e0.preventDefault();
        e0.stopPropagation();

        const grid     = document.querySelector('.dashboard-grid');
        const startY   = e0.clientY;
        const startH   = widgetEl.getBoundingClientRect().height;

        // 이벤트 가로채기 방지용 오버레이
        const cover = document.createElement('div');
        cover.style.cssText = 'position:fixed;inset:0;z-index:9999;cursor:nwse-resize;';
        document.body.appendChild(cover);

        function onMove(e) {
            const h = Math.max(120, startH + (e.clientY - startY));
            widgetEl.style.height = h + 'px';
        }

        function onUp(e) {
            document.removeEventListener('mousemove', onMove);
            document.removeEventListener('mouseup',  onUp);
            cover.remove();

            const gw   = grid ? grid.offsetWidth : window.innerWidth;
            const rect = widgetEl.getBoundingClientRect();
            const frac = (e.clientX - rect.left) / gw;

            const size = frac < 0.375 ? '1/4'
                       : frac < 0.625 ? '1/2'
                       : frac < 0.875 ? '3/4'
                       : '전체';

            const finalH = Math.max(120, startH + (e.clientY - startY));
            dotnetRef.invokeMethodAsync('OnWidgetResized', widgetId, size, finalH);
        }

        document.addEventListener('mousemove', onMove);
        document.addEventListener('mouseup',   onUp);
    });
};
