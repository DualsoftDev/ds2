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
