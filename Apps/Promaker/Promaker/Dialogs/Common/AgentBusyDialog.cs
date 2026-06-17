using System;
using System.Windows;
using System.Windows.Controls;

namespace Promaker.Dialogs;

/// <summary>Agent 가 이미 PLC 를 모니터링(5051 점유)하는 중에 Control PLAY 를 누른 경우의 사용자 선택.</summary>
public enum AgentBusyChoice
{
    /// <summary>취소 — PLAY 중단.</summary>
    Cancel,
    /// <summary>기존 5051 Agent 를 Control 세션으로 전환(재시작)해 실 PLC 를 제어.</summary>
    SwitchToControl,
    /// <summary>새 포트로 Promaker 자체 Hub 를 띄워 모델만 가상 Control/VP 시험 (실 PLC 미접속).</summary>
    NewVirtualHub,
}

/// <summary>
/// Agent 가 이미 이 PLC 를 모니터링 중(5051 점유)일 때 Control PLAY 진입 시 띄우는 선택 다이얼로그.
/// 코드비하인드 동적 구성 (AgentDelegationNoticeDialog 와 동일 스타일).
/// </summary>
internal static class AgentBusyDialog
{
    /// <summary>modal 표시 후 사용자의 선택 반환. 창을 닫거나 취소하면 <see cref="AgentBusyChoice.Cancel"/>.</summary>
    internal static AgentBusyChoice Ask()
    {
        var dialog = new Window
        {
            Title = "Agent 모니터링 중 — Control 시작",
            Width = 560,
            Height = 380,
            SizeToContent = SizeToContent.Manual,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current?.MainWindow,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };

        var msg = new TextBlock
        {
            Text =
                "Agent 가 이미 이 PLC 를 모니터링 중입니다 (5051).\n\n어떻게 진행할까요?",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(20, 20, 20, 14),
            FontSize = 13,
        };

        var result = AgentBusyChoice.Cancel;

        var switchBtn = MakeChoiceButton(
            "이 PLC 를 Control 로 전환",
            "Agent 를 Control 세션으로 재시작합니다. 실 PLC 에 OUT 을 씁니다. "
          + "모니터링은 잠깐 끊겼다 자동 복구됩니다.");
        var newHubBtn = MakeChoiceButton(
            "새 포트로 Hub 띄우기 (가상)",
            "실 PLC 모니터링(5051)은 그대로 유지합니다. Promaker 는 새 포트에서 모델 로직만 시험합니다. "
          + "실 PLC 는 건드리지 않으며, 정지를 누르면 꺼집니다.");

        switchBtn.Click += (_, _) => { result = AgentBusyChoice.SwitchToControl; dialog.DialogResult = true; };
        newHubBtn.Click += (_, _) => { result = AgentBusyChoice.NewVirtualHub; dialog.DialogResult = true; };

        var cancelBtn = new Button { Content = "취소", Width = 90, IsCancel = true };
        if (Application.Current?.TryFindResource("DarkButton") is Style cancelStyle)
            cancelBtn.Style = cancelStyle;
        cancelBtn.Click += (_, _) => { result = AgentBusyChoice.Cancel; dialog.DialogResult = false; };

        var cancelPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(20, 6, 20, 16),
        };
        cancelPanel.Children.Add(cancelBtn);

        var root = new StackPanel();
        root.Children.Add(msg);
        root.Children.Add(switchBtn);
        root.Children.Add(newHubBtn);
        root.Children.Add(cancelPanel);
        dialog.Content = root;

        dialog.ShowDialog();
        return result;
    }

    /// <summary>제목 + 설명 2줄을 담은 큰 선택 버튼.</summary>
    private static Button MakeChoiceButton(string title, string detail)
    {
        var btn = new Button
        {
            Margin = new Thickness(20, 0, 20, 10),
            Padding = new Thickness(14, 10, 14, 10),
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        if (Application.Current?.TryFindResource("DarkButton") is Style darkStyle)
            btn.Style = darkStyle;

        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold });
        sp.Children.Add(new TextBlock
        {
            Text = detail,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            Opacity = 0.85,
        });
        btn.Content = sp;
        return btn;
    }
}
