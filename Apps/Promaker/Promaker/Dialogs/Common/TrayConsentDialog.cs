using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Promaker.Services;

namespace Promaker.Dialogs;

/// <summary>
/// Monitoring + IsRealPlcConnected 인 상태에서 PLAY 클릭 시 표시되는 확인 다이얼로그.
/// "다시 묻지 않기" 체크 시 SettingsPaths.TrayConsentSuppress 에 빈 파일 저장 → 다음부터 자동 진행.
/// </summary>
internal static class TrayConsentDialog
{
    /// <summary>이전에 "다시 묻지 않기" 가 체크된 적이 있는지.</summary>
    internal static bool IsSuppressed() => File.Exists(SettingsPaths.TrayConsentSuppress);

    /// <summary>
    /// 다이얼로그 표시. 반환 true = 사용자가 확인. false = 취소(시작 중단).
    /// 사용자가 "다시 묻지 않기" 체크 + 확인 누른 경우 영속화.
    /// </summary>
    internal static bool ShowAndAskConsent()
    {
        if (IsSuppressed()) return true; // 이미 동의 — 다이얼로그 생략

        var dialog = new Window
        {
            Title = "백그라운드 전환 안내",
            Width = 480,
            Height = 320,
            SizeToContent = SizeToContent.Manual,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };

        var msg = new TextBlock
        {
            Text = "Monitoring 모드 + PLC 캡쳐 시작 시\n메인 창이 백그라운드(트레이)로 전환되며\nDSPilot 웹 대시보드가 기본 브라우저로 열립니다.\n\n"
                 + "시스템 트레이 아이콘을 더블클릭하면 언제든 창을 다시 열 수 있고,\n"
                 + "트레이 우클릭 → \"DSPilot 접속\" 으로 다시 브라우저를 띄울 수 있습니다.\n\n"
                 + "계속하시겠습니까?",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(20, 20, 20, 12),
            FontSize = 13,
        };

        var noAskCheck = new CheckBox
        {
            Content = "다시 묻지 않기 (Settings 파일 삭제로 초기화 가능)",
            Margin = new Thickness(20, 0, 20, 12),
            FontSize = 12,
        };

        var okButton = new Button { Content = "확인 (트레이로 전환 후 시작)", Width = 230, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancelButton = new Button { Content = "취소", Width = 80, IsCancel = true };
        if (Application.Current.TryFindResource("DarkButton") is Style darkStyle)
        {
            okButton.Style = darkStyle;
            cancelButton.Style = darkStyle;
        }

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(20, 0, 20, 16),
        };
        buttonPanel.Children.Add(okButton);
        buttonPanel.Children.Add(cancelButton);

        var root = new StackPanel();
        root.Children.Add(msg);
        root.Children.Add(noAskCheck);
        root.Children.Add(buttonPanel);
        dialog.Content = root;

        bool? result = null;
        bool suppressNext = false;
        okButton.Click += (_, _) =>
        {
            result = true;
            suppressNext = noAskCheck.IsChecked == true;
            dialog.DialogResult = true;
        };
        cancelButton.Click += (_, _) =>
        {
            result = false;
            dialog.DialogResult = false;
        };

        dialog.ShowDialog();

        if (result == true && suppressNext)
        {
            try
            {
                var path = SettingsPaths.TrayConsentSuppress;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, DateTime.UtcNow.ToString("O"));
            }
            catch { /* best-effort */ }
        }

        return result == true;
    }
}
