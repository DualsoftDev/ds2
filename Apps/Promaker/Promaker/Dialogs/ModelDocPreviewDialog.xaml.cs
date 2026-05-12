using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Ds2.LlmAgent;
using log4net;

namespace Promaker.Dialogs;

/// <summary>
/// chat-ui boost (Mermaid 직접 렌더링): `apply_model_doc` 발행 doc 의 YAML view + Mermaid view (work-flow / call-flow).
/// Mermaid 텍스트 생성은 F# `ModelProtocolMermaid.jsonElementToBlocks` (Ev2 패턴 차용 — theme/classDef/Reset connector).
/// 렌더링 = WebView2 (`wwwroot/mermaid-view.html`) — mermaid.js v11 CDN 로딩 후 PostWebMessageAsJson 으로 block list 전달.
/// passive-only / patch-only doc 은 Mermaid block 0건 → Mermaid tab 자체 미생성 (YAML tab 만).
///
/// **1회용 전제**: 본 dialog 는 doc 1건에 대한 ShowDialog 후 close. _pendingBlocksJson 은 한 번만 flush 됨 —
/// dialog 인스턴스 재사용 (Show / Hide 반복) 시 두번째 doc 표시 안 됨. 재사용 시나리오 등장 시 reset 로직 필요.
/// </summary>
public partial class ModelDocPreviewDialog : Window
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(ModelDocPreviewDialog));

    private const string VirtualHostName = "promaker.dialog";
    private const string MermaidHtmlPage = "mermaid-view.html";

    private readonly string _yaml;
    private string? _pendingBlocksJson;
    private bool _webViewReady;

    public ModelDocPreviewDialog(string yaml)
    {
        InitializeComponent();
        _yaml = yaml ?? string.Empty;
        YamlBox.Text = _yaml;
        var lineCount = string.IsNullOrEmpty(_yaml) ? 0 : _yaml.Split('\n').Length;
        var byteCount = System.Text.Encoding.UTF8.GetByteCount(_yaml);
        MetaText.Text = $"{lineCount} lines · {FormatBytes(byteCount)}";

        var blocksJson = TryBuildBlocksJson();
        if (blocksJson is null)
        {
            // passive-only / patch-only / parse 실패 — Mermaid tab 자체 미생성.
            HideMermaidTab();
            return;
        }
        _pendingBlocksJson = blocksJson;
        Loaded += OnDialogLoaded;
        // WebView2 process 즉시 회수 (chromium child) — WPF GC 기다리지 않음.
        Closed += OnDialogClosed;
    }

    private void OnDialogClosed(object? sender, EventArgs e)
    {
        Closed -= OnDialogClosed;
        try
        {
            if (MermaidWebView.CoreWebView2 != null)
                MermaidWebView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
            MermaidWebView.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Mermaid WebView2 dispose 실패", ex);
        }
    }

    /// <summary>YAML → JSON → MermaidBlock list → JSON 직렬화 (HTML 측 schema 와 동형).
    /// null 반환 = Mermaid tab 미생성 (block 0 또는 parse 실패).</summary>
    private string? TryBuildBlocksJson()
    {
        if (string.IsNullOrWhiteSpace(_yaml)) return null;
        try
        {
            using var jdoc = ModelProtocolYaml.yamlToJson(_yaml);
            var blocks = ModelProtocolMermaid.jsonElementToBlocks(jdoc.RootElement);
            if (blocks == null || !blocks.Any()) return null;
            var payload = blocks
                .Select(b => new { title = b.Title, mermaid = b.Mermaid })
                .ToArray();
            return JsonSerializer.Serialize(new { type = "render", blocks = payload });
        }
        catch (Exception ex)
        {
            Log.Warn($"발행 doc Mermaid 변환 실패 — {ex.Message}", ex);
            return null;
        }
    }

    private void HideMermaidTab()
    {
        MermaidTab.Visibility = Visibility.Collapsed;
        ViewTabs.SelectedIndex = 0;
    }

    private async void OnDialogLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnDialogLoaded;
        try
        {
            await MermaidWebView.EnsureCoreWebView2Async();

            var wwwroot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
            MermaidWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                VirtualHostName,
                wwwroot,
                CoreWebView2HostResourceAccessKind.Allow);

            MermaidWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            MermaidWebView.Source = new Uri($"https://{VirtualHostName}/{MermaidHtmlPage}");
        }
        catch (Exception ex)
        {
            // WebView2 Runtime 미설치 등 — 빈 검은 영역 대신 안내 표시 + log.
            Log.Warn($"Mermaid WebView2 초기화 실패 — {ex.Message}", ex);
            ShowWebView2InitError(ex.Message);
        }
    }

    /// <summary>WebView2 초기화 실패 시 Mermaid tab 안에 안내 TextBlock 표시 (사용자 친화 fallback).</summary>
    private void ShowWebView2InitError(string detail)
    {
        var msg = new System.Windows.Controls.TextBlock
        {
            Text = $"Mermaid 렌더링 초기화 실패 — WebView2 Runtime 이 설치되어 있는지 확인하세요.\n\n{detail}",
            TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(16),
            Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush"),
            FontSize = 12,
        };
        MermaidTab.Content = msg;
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // HTML 측에서 보내는 메시지 — { type: 'ready' | 'copy', ... }.
        try
        {
            using var jdoc = JsonDocument.Parse(e.WebMessageAsJson);
            if (!jdoc.RootElement.TryGetProperty("type", out var typeEl)) return;
            var type = typeEl.GetString();
            if (type == "ready")
            {
                _webViewReady = true;
                FlushPendingBlocks();
            }
            else if (type == "copy" && jdoc.RootElement.TryGetProperty("text", out var textEl))
            {
                // HTML 측 navigator.clipboard 차단 시 C# fallback.
                var text = textEl.GetString() ?? string.Empty;
                try { Clipboard.SetText(text); }
                catch (Exception ex) { Log.Warn("Mermaid block C# fallback 복사 실패", ex); }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Mermaid WebView2 메시지 파싱 실패", ex);
        }
    }

    private void FlushPendingBlocks()
    {
        if (!_webViewReady || _pendingBlocksJson is null) return;
        if (MermaidWebView.CoreWebView2 == null) return;
        MermaidWebView.CoreWebView2.PostWebMessageAsJson(_pendingBlocksJson);
        _pendingBlocksJson = null;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_yaml);
        }
        catch (Exception ex)
        {
            // 클립보드 동시성 race — silent fail (사용자가 dialog 다시 열어 재시도 가능).
            Log.Warn("YAML 전체 복사 실패", ex);
        }
    }

    private static string FormatBytes(int bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        return $"{bytes / 1024.0:F1} KB";
    }
}
