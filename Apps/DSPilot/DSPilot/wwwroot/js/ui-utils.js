// DSPilot UI Utilities — Snackbar + Confirm Dialog (MudBlazor replacement)
window.uiUtils = {
    _elementSizeObservers: new Map(),

    // ── Snackbar (Toast) ─────────────────────────
    showSnackbar: function (message, severity, durationMs) {
        durationMs = durationMs || 3000;
        var container = document.getElementById('snackbar-container');
        if (!container) return;

        var colorMap = {
            success: '#4CAF50', error: '#F44336', warning: '#FF9800', info: '#009BB4'
        };
        var iconMap = {
            success: 'check_circle', error: 'error', warning: 'warning', info: 'info'
        };
        var sev = (severity || 'info').toLowerCase();
        var bg = colorMap[sev] || colorMap.info;
        var icon = iconMap[sev] || iconMap.info;

        var el = document.createElement('div');
        el.className = 'snackbar snackbar-show';
        el.style.background = bg;
        el.innerHTML = '<span class="material-icons" style="font-size:18px;margin-right:8px;">' + icon + '</span>' +
                       '<span>' + message + '</span>';

        container.appendChild(el);

        setTimeout(function () {
            el.classList.remove('snackbar-show');
            el.classList.add('snackbar-hide');
            setTimeout(function () { el.remove(); }, 300);
        }, durationMs);
    },

    // ── Confirm Dialog ───────────────────────────
    showConfirm: function (title, message, yesText, cancelText) {
        return new Promise(function (resolve) {
            var overlay = document.createElement('div');
            overlay.className = 'dialog-overlay';

            var dialog = document.createElement('div');
            dialog.className = 'dialog';
            dialog.innerHTML =
                '<div class="dialog-title">' + (title || '확인') + '</div>' +
                '<div class="dialog-body">' + (message || '') + '</div>' +
                '<div class="dialog-actions">' +
                    '<button class="btn btn-outlined dialog-cancel">' + (cancelText || '취소') + '</button>' +
                    '<button class="btn btn-filled dialog-confirm">' + (yesText || '확인') + '</button>' +
                '</div>';

            overlay.appendChild(dialog);
            document.body.appendChild(overlay);

            // Force reflow then show
            overlay.offsetHeight;
            overlay.classList.add('dialog-visible');

            var close = function (result) {
                overlay.classList.remove('dialog-visible');
                setTimeout(function () { overlay.remove(); }, 200);
                resolve(result);
            };

            dialog.querySelector('.dialog-confirm').addEventListener('click', function () { close(true); });
            dialog.querySelector('.dialog-cancel').addEventListener('click', function () { close(false); });
            overlay.addEventListener('click', function (e) { if (e.target === overlay) close(false); });
        });
    },

    observeElementSize: function (element, dotNetRef, callbackMethodName) {
        if (!element || !dotNetRef || !callbackMethodName) return;

        this.disposeElementSizeObserver(element);

        var notify = function () {
            var width = Math.round(element.clientWidth || element.getBoundingClientRect().width || 0);
            if (width > 0) {
                dotNetRef.invokeMethodAsync(callbackMethodName, width).catch(function () { });
            }
        };

        var entry = {
            resizeObserver: null,
            onWindowResize: notify
        };

        if (window.ResizeObserver) {
            entry.resizeObserver = new ResizeObserver(function () {
                notify();
            });
            entry.resizeObserver.observe(element);
        }

        window.addEventListener('resize', entry.onWindowResize, { passive: true });
        this._elementSizeObservers.set(element, entry);
        notify();
    },

    // ── Fullscreen API ───────────────────────────
    enterFullscreen: function (element, dotNetRef) {
        if (!element) return;
        // 브라우저 전체화면 진입
        var fn = element.requestFullscreen || element.webkitRequestFullscreen || element.msRequestFullscreen;
        if (fn) fn.call(element);

        // fullscreenchange 이벤트로 ESC/브라우저 종료 감지
        if (dotNetRef) {
            var handler = function () {
                if (!document.fullscreenElement && !document.webkitFullscreenElement) {
                    dotNetRef.invokeMethodAsync('OnBrowserFullscreenExited').catch(function () { });
                    document.removeEventListener('fullscreenchange', handler);
                    document.removeEventListener('webkitfullscreenchange', handler);
                }
            };
            document.addEventListener('fullscreenchange', handler);
            document.addEventListener('webkitfullscreenchange', handler);
        }
    },

    exitFullscreen: function () {
        var fn = document.exitFullscreen || document.webkitExitFullscreen || document.msExitFullscreen;
        if (fn && (document.fullscreenElement || document.webkitFullscreenElement)) {
            fn.call(document);
        }
    },

    disposeElementSizeObserver: function (element) {
        if (!element) return;

        var entry = this._elementSizeObservers.get(element);
        if (!entry) return;

        if (entry.resizeObserver) {
            entry.resizeObserver.disconnect();
        }

        if (entry.onWindowResize) {
            window.removeEventListener('resize', entry.onWindowResize);
        }

        this._elementSizeObservers.delete(element);
    }
};

// ── Flow Layout auto-orient: 부모 컨테이너 가용 영역의 가로/세로 비를 측정해
//    Blazor 컴포넌트(FlowLayoutSvg) 에 알려주어 Col/Row 전치(transpose) 결정에 사용.
window.flowLayoutAutoOrient = {
    _observers: new Map(),

    observe: function (element, dotNetRef) {
        if (!element || !dotNetRef) return;
        this.unobserve(element);

        var notify = function () {
            var parent = element.parentElement;
            if (!parent) return;
            var w = parent.clientWidth || parent.getBoundingClientRect().width || 0;
            var h = parent.clientHeight || parent.getBoundingClientRect().height || 0;
            if (w <= 0 || h <= 0) return;
            dotNetRef.invokeMethodAsync('OnContainerAspect', w, h).catch(function () { });
        };

        var entry = { ro: null, onResize: notify };
        if (window.ResizeObserver) {
            entry.ro = new ResizeObserver(notify);
            var parent = element.parentElement;
            if (parent) entry.ro.observe(parent);
            entry.ro.observe(element);
        }
        window.addEventListener('resize', notify, { passive: true });
        this._observers.set(element, entry);
        // 첫 측정은 레이아웃 정착 후 실행
        setTimeout(notify, 0);
    },

    unobserve: function (element) {
        if (!element) return;
        var entry = this._observers.get(element);
        if (!entry) return;
        if (entry.ro) entry.ro.disconnect();
        if (entry.onResize) window.removeEventListener('resize', entry.onResize);
        this._observers.delete(element);
    }
};
