using System;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Promaker.Windows;

public partial class CustomModelDialog
{
    private const string VirtualHostName = "promaker-preview.app";
    private const string PreviewHtmlPage = "device-preview.html";

    private bool _previewReady;

    private async Task InitializePreviewWebView()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync();
            await PreviewWebView.EnsureCoreWebView2Async(env);

            PreviewWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                VirtualHostName, _wwwrootPath,
                CoreWebView2HostResourceAccessKind.Allow);

            PreviewWebView.NavigationCompleted += (_, _) =>
            {
                _previewReady = true;
                UpdatePreview();
            };

            PreviewWebView.CoreWebView2.Navigate($"https://{VirtualHostName}/{PreviewHtmlPage}?embed");
        }
        catch (Exception ex)
        {
            ShowMessage($"프리뷰 초기화 실패: {ex.Message}", true);
        }
    }

    /// <summary>
    /// SystemType 이름이 비어있으면 JSON 입력 / 파일 불러오기 / AI 프롬프트 / 프리뷰를 비활성화.
    /// 숨기지 않고 불투명도로 죽여서 중앙 힌트로 안내.
    /// WebView2는 native HWND(airspace)라 Opacity가 먹히지 않으므로 Visibility=Hidden으로 처리
    /// — 프리뷰 Border 프레임은 dim된 채로 남아 "꺼짐" 느낌 유지.
    /// </summary>
    private void UpdateJsonEditorEnabled()
    {
        if (JsonEditor == null) return;
        var hasType = !string.IsNullOrEmpty(GetSystemType());
        var dimOpacity = 0.35;

        JsonEditor.IsEnabled = hasType;
        if (LoadFileButton != null) LoadFileButton.IsEnabled = hasType;
        if (AIPromptButton != null) AIPromptButton.IsEnabled = hasType;

        if (JsonBorder != null) JsonBorder.Opacity = hasType ? 1.0 : dimOpacity;
        if (PreviewBorder != null)
        {
            PreviewBorder.Opacity = hasType ? 1.0 : dimOpacity;
            PreviewBorder.IsEnabled = hasType;
        }
        if (PreviewWebView != null)
            PreviewWebView.Visibility = hasType ? Visibility.Visible : Visibility.Hidden;
        if (DisabledHint != null)
            DisabledHint.Visibility = hasType ? Visibility.Collapsed : Visibility.Visible;
    }

    private void JsonEditor_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        ValidateForm();
        _debounceTimer?.Stop();
        _debounceTimer?.Start();
    }

    private void UpdatePreview()
    {
        if (!_previewReady) return;
        var json = JsonEditor.Text?.Trim();
        if (string.IsNullOrEmpty(json)) return;

        try
        {
            // preview.html의 전역 함수 호출: JSON을 파싱하여 렌더링
            var escaped = json
                .Replace("\\", "\\\\")
                .Replace("`", "\\`")
                .Replace("${", "\\${");
            PreviewWebView.CoreWebView2.ExecuteScriptAsync(
                $"try {{ var _s = JSON.parse(`{escaped}`); buildDevice(_s); setState('R'); }} catch(e) {{ }}");
        }
        catch
        {
            // 프리뷰 갱신 실패는 무시 (JSON이 아직 불완전할 수 있음)
        }
    }
}
