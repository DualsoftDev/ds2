using System;
using System.Windows;

namespace Promaker.Dialogs;

/// <summary>
/// chat-ui boost: `apply_model_doc` 발행 doc 의 YAML view dialog.
/// LlmChatViewModel 의 turn end 처리에서 model doc line count > 30 시 button bubble 표시,
/// 사용자 클릭 → 본 dialog 띄움. 후속 확장 (syntax highlight, diff view) 은 YamlBox 교체로 가능.
/// </summary>
public partial class ModelDocPreviewDialog : Window
{
    private readonly string _yaml;

    public ModelDocPreviewDialog(string yaml)
    {
        InitializeComponent();
        _yaml = yaml ?? string.Empty;
        YamlBox.Text = _yaml;
        var lineCount = string.IsNullOrEmpty(_yaml) ? 0 : _yaml.Split('\n').Length;
        var byteCount = System.Text.Encoding.UTF8.GetByteCount(_yaml);
        MetaText.Text = $"{lineCount} lines · {FormatBytes(byteCount)}";
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_yaml);
        }
        catch (Exception)
        {
            // 클립보드 동시성 race — 사용자에게 silent fail (dialog 가 닫히면 재시도 가능).
        }
    }

    private static string FormatBytes(int bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        return $"{bytes / 1024.0:F1} KB";
    }
}
