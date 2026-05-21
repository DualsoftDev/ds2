using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Promaker.Services;

namespace Promaker.Dialogs;

/// <summary>
/// Monitoring + 실 PLC PLAY 시 DSPilot 이 설치되어 있지 않아 브라우저 자동 실행을 건너뛸 때 표시하는 안내.
/// 모니터링 자체는 정상 동작하지만 대시보드 접속이 불가능함을 알린다.
/// "다시 보지 않기" 체크 시 SettingsPaths.DspilotMissingNoticeSuppress 에 빈 파일 저장 → 다음부터 생략.
/// </summary>
internal static class DspilotMissingNoticeDialog
{
    internal static bool IsSuppressed() => File.Exists(SettingsPaths.DspilotMissingNoticeSuppress);

    internal static void Show()
    {
        if (IsSuppressed()) return;

        var dialog = new Window
        {
            Title = "DSPilot 미설치 안내",
            Width = 500,
            Height = 280,
            SizeToContent = SizeToContent.Manual,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };

        var msg = new TextBlock
        {
            Text =
                "DSPilot 이 설치되어 있지 않습니다.\n\n"
              + "Promaker 의 모니터링은 정상적으로 시작되었지만,\n"
              + "DSPilot 웹 대시보드 자동 접속은 건너뛰었습니다.\n\n"
              + "대시보드를 사용하시려면 DSPilot 인스톨러를 실행해 주세요.\n"
              + "(설치 후 다음 PLAY 부터 기본 브라우저가 자동으로 열립니다.)",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(20, 20, 20, 12),
            FontSize = 13,
        };

        var noAskCheck = new CheckBox
        {
            Content = "다시 보지 않기 (Settings 파일 삭제로 초기화 가능)",
            Margin = new Thickness(20, 0, 20, 12),
            FontSize = 12,
        };

        var okButton = new Button { Content = "확인", Width = 100, IsDefault = true };
        if (Application.Current.TryFindResource("DarkButton") is Style darkStyle)
            okButton.Style = darkStyle;

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(20, 0, 20, 16),
        };
        buttonPanel.Children.Add(okButton);

        var root = new StackPanel();
        root.Children.Add(msg);
        root.Children.Add(noAskCheck);
        root.Children.Add(buttonPanel);
        dialog.Content = root;

        var suppressNext = false;
        okButton.Click += (_, _) =>
        {
            suppressNext = noAskCheck.IsChecked == true;
            dialog.DialogResult = true;
        };

        dialog.ShowDialog();

        if (suppressNext)
        {
            try
            {
                var path = SettingsPaths.DspilotMissingNoticeSuppress;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, DateTime.UtcNow.ToString("O"));
            }
            catch { /* best-effort */ }
        }
    }
}
