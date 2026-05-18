using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Promaker.Presentation;

namespace Promaker.Windows;

public partial class View3DWindow
{
    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await WebView3D.EnsureCoreWebView2Async();

            // wwwroot 폴더를 가상 호스트에 매핑
            var wwwroot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
            WebView3D.CoreWebView2.SetVirtualHostNameToFolderMapping(
                VirtualHostName,
                wwwroot,
                CoreWebView2HostResourceAccessKind.Allow);

            // JS → C# 콜백 수신 (선택 이벤트)
            WebView3D.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            // C# → JS 전송 델리게이트 주입
            _vm.SetWebViewSender(async json =>
            {
                if (WebView3D.CoreWebView2 != null)
                    await WebView3D.CoreWebView2.ExecuteScriptAsync(
                        $"handleFromCSharp({json})");
            });

            // 페이지 로드 완료 후 씬 빌드
            WebView3D.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

            // facility3d.html 로드
            WebView3D.Source = new Uri($"https://{VirtualHostName}/{FacilityHtmlPage}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[View3DWindow] WebView2 init failed: {ex.Message}");
        }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        WebView3D.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;

        // 페이지 로드 직후 — init 메시지보다 먼저 테마를 적용해 흰 배경 깜빡임 방지.
        _webViewReady = true;
        PushThemeToWebView(ThemeManager.CurrentTheme);

        if (_onReady != null)
        {
            try { await _onReady(); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[View3DWindow] onReady failed: {ex.Message}");
            }
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var raw = e.TryGetWebMessageAsString();
            if (string.IsNullOrEmpty(raw)) return;

            var doc = JsonDocument.Parse(raw);
            var method = doc.RootElement.GetProperty("method").GetString() ?? "";
            var argsEl = doc.RootElement.GetProperty("args");
            var args = argsEl.EnumerateArray().ToArray();

            if (method == "RebuildWithAutoLayout")
            {
                if (_store != null)
                    _ = _vm.RebuildWithAutoLayout(_store, _projectId);
                return;
            }

            _vm.OnSelectionMessage(method, args);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[View3DWindow] WebMessage parse error: {ex.Message}");
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        ThemeManager.ThemeChanged -= OnPromakerThemeChanged;
        _webViewReady = false;
        try { _ = WebView3D.CoreWebView2?.ExecuteScriptAsync("if(window.Ds2Sound)Ds2Sound.stopAll();"); }
        catch { }
        if (WebView3D.CoreWebView2 != null)
        {
            WebView3D.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
        }
        _vm.SetWebViewSender(null);
    }
}
