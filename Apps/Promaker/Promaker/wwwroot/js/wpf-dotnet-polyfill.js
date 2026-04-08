// WPF WebView2 폴리필 — Blazor DotNet.invokeMethodAsync 콜백을
// WebView2 window.chrome.webview.postMessage 로 리다이렉트한다.
window._wpfCallbackRef = {
    invokeMethodAsync: function(methodName) {
        var args = Array.prototype.slice.call(arguments, 1);
        try {
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage(JSON.stringify({
                    method: methodName,
                    args: args
                }));
            }
        } catch (e) {
            console.warn('wpf-dotnet-polyfill: postMessage failed', e);
        }
        return Promise.resolve(null);
    }
};
