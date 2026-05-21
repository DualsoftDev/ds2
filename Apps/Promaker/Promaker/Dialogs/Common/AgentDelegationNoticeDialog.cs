using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Promaker.Services;

namespace Promaker.Dialogs;

/// <summary>
/// Monitoring + 실 PLC PLAY 시점에 Promaker.Agent (Windows Service) 가 모니터링을 (재)시작했음을
/// 알리는 안내 다이얼로그. "다시 보지 않기" 체크 시 SettingsPaths.AgentDelegationNoticeSuppress 에
/// 빈 파일 저장 → 다음부터 SimLog 한 줄만 남기고 다이얼로그 생략.
/// </summary>
internal static class AgentDelegationNoticeDialog
{
    /// <summary>이전에 "다시 보지 않기" 가 체크된 적이 있는지.</summary>
    internal static bool IsSuppressed() => File.Exists(SettingsPaths.AgentDelegationNoticeSuppress);

    /// <summary>
    /// 다이얼로그 표시 (modal). 사용자가 "다시 보지 않기" 체크 후 확인 시 영속화.
    /// 이미 suppress 상태면 no-op (호출자는 IsSuppressed 로 사전 가드 권장).
    /// </summary>
    internal static void Show()
    {
        if (IsSuppressed()) return;

        var dialog = new Window
        {
            Title = "모니터링 위임 안내",
            Width = 520,
            Height = 340,
            SizeToContent = SizeToContent.Manual,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };

        var msg = new TextBlock
        {
            Text =
                "Promaker.Agent (Windows 서비스) 가 모니터링을 (재)시작했습니다.\n\n"
              + "PLC 스캔과 SignalR Hub(5051, 읽기 전용) 호스팅은 Agent 가 전담합니다.\n"
              + "Promaker 본체는 5051 의 클라이언트로만 동작합니다.\n\n"
              + "Agent 의 실시간 상태(● 모니터링 중 / ○ 대기 / ✗ 정지)는\n"
              + "시스템 트레이의 'Promaker Agent' 아이콘에서 확인하세요.\n"
              + "우클릭 메뉴로 모니터링 시작/정지 + 재부팅 시 자동 실행 토글이 가능합니다.",
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
                var path = SettingsPaths.AgentDelegationNoticeSuppress;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, DateTime.UtcNow.ToString("O"));
            }
            catch { /* best-effort */ }
        }
    }
}
