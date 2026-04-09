using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Promaker.Dialogs;

internal enum WarningSeverity { Red, Yellow }

internal record GraphWarningSection(
    string Title,
    WarningSeverity Severity,
    List<string> Lines,
    string? Detail = null);

internal static class DialogHelpers
{
    internal const string IconWarn  = "⚠";
    internal const string IconInfo  = "ℹ";
    internal const string IconError = "✖";
    internal const string IconQuestion = "?";

    internal static bool ShowOwnedDialog(Window dialog)
    {
        dialog.Owner = Application.Current.MainWindow;
        dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        return dialog.ShowDialog() == true;
    }

    internal static void Warn(string message) =>
        Warn(Application.Current.MainWindow, message);

    internal static void Warn(Window? owner, string message, string title = "경고") =>
        ShowThemedMessageBox(owner, message, title, MessageBoxButton.OK, IconWarn);

    internal static void Info(string message) =>
        Info(Application.Current.MainWindow, message);

    internal static void Info(Window? owner, string message, string title = "알림") =>
        ShowThemedMessageBox(owner, message, title, MessageBoxButton.OK, IconInfo);

    internal static void Error(Window? owner, string message, string title = "오류") =>
        ShowThemedMessageBox(owner, message, title, MessageBoxButton.OK, IconError);

    internal static bool Confirm(Window? owner, string message, string title) =>
        ShowThemedMessageBox(owner, message, title, MessageBoxButton.YesNo, IconQuestion) == MessageBoxResult.Yes;

    internal static string? PromptName(string title, string defaultName)
    {
        var dialog = new Window
        {
            Title = title, Width = 350, Height = 150, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow, ResizeMode = ResizeMode.NoResize
        };
        var textBox = new TextBox { Text = defaultName, Margin = new Thickness(12, 12, 12, 0) };
        textBox.SelectAll();
        var okButton = new Button { Content = "OK", Width = 80, Margin = new Thickness(0, 8, 0, 12), IsDefault = true };
        var cancelButton = new Button { Content = "Cancel", Width = 80, Margin = new Thickness(8, 8, 12, 12), IsCancel = true };
        if (Application.Current.TryFindResource("DarkButton") is Style darkStyle)
        {
            okButton.Style = darkStyle;
            cancelButton.Style = darkStyle;
        }
        string? result = null;
        okButton.Click += (_, _) => { result = textBox.Text.Trim(); dialog.DialogResult = true; };
        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttonPanel.Children.Add(okButton);
        buttonPanel.Children.Add(cancelButton);
        var panel = new StackPanel();
        panel.Children.Add(textBox);
        panel.Children.Add(buttonPanel);
        dialog.Content = panel;
        dialog.Loaded += (_, _) => { textBox.Focus(); };
        return dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(result) ? result : null;
    }

    internal static void PickCallFromList(string title, IReadOnlyList<(Guid Id, string Name)> calls,
        Action<Guid> onNavigate)
    {
        var dialog = new Window
        {
            Title = title, Width = 380, Height = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow, ResizeMode = ResizeMode.CanResize,
            MinWidth = 280, MinHeight = 200
        };

        var listBox = new ListBox
        {
            Margin = new Thickness(8),
            DisplayMemberPath = "Name",
            FontSize = 13
        };

        foreach (var call in calls)
            listBox.Items.Add(new { call.Id, call.Name });

        listBox.MouseDoubleClick += (_, e) =>
        {
            if (listBox.SelectedItem is not null)
            {
                var id = ((dynamic)listBox.SelectedItem).Id;
                onNavigate((Guid)id);
                dialog.Close();
            }
        };

        dialog.Content = listBox;
        dialog.ShowDialog();
    }

    /// <summary>그래프 검증 경고를 심각도별 색상으로 표시합니다.</summary>
    internal static void ShowGraphWarnings(List<GraphWarningSection> sections)
    {
        if (sections.Count == 0) return;

        var bgBrush = (Brush?)Application.Current.TryFindResource("SecondaryBackgroundBrush")
                      ?? SystemColors.WindowBrush;
        var fgBrush = (Brush?)Application.Current.TryFindResource("PrimaryTextBrush")
                      ?? SystemColors.ControlTextBrush;
        var dialog = new Window
        {
            Title = "그래프 검증 경고",
            Width = 480,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = bgBrush,
            Foreground = fgBrush
        };

        var darkButtonStyle = Application.Current.TryFindResource("DarkButton") as Style;

        var textBlock = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Margin = new Thickness(0)
        };

        var isDark = Presentation.ThemeManager.CurrentTheme == Presentation.AppTheme.Dark;
        var redColor = Brushes.OrangeRed;
        var yellowColor = isDark ? Brushes.Gold : Brushes.DarkOrange;

        foreach (var section in sections)
        {
            var titleColor = section.Severity == WarningSeverity.Red
                ? redColor
                : yellowColor;

            textBlock.Inlines.Add(new Run($"[{section.Title}]") { Foreground = titleColor, FontWeight = FontWeights.Bold });
            textBlock.Inlines.Add(new LineBreak());
            foreach (var line in section.Lines)
            {
                textBlock.Inlines.Add(new Run(line) { Foreground = fgBrush });
                textBlock.Inlines.Add(new LineBreak());
            }
            if (!string.IsNullOrWhiteSpace(section.Detail))
            {
                textBlock.Inlines.Add(new Run(section.Detail) { Foreground = titleColor, FontSize = 11 });
                textBlock.Inlines.Add(new LineBreak());
            }
            textBlock.Inlines.Add(new LineBreak());
        }

        textBlock.Inlines.Add(new Run("시뮬레이션은 계속 진행됩니다.") { Foreground = fgBrush });

        var iconBlock = new TextBlock
        {
            Text = IconWarn,
            FontSize = 28,
            Foreground = Brushes.Gold,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 12, 0)
        };

        var scrollViewer = new ScrollViewer
        {
            Content = textBlock,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 350
        };

        var contentPanel = new Grid { Margin = new Thickness(16, 16, 16, 12) };
        contentPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        contentPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(iconBlock, 0);
        Grid.SetColumn(scrollViewer, 1);
        contentPanel.Children.Add(iconBlock);
        contentPanel.Children.Add(scrollViewer);

        var okButton = new Button
        {
            Content = "확인", MinWidth = 70, Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(0, 0, 16, 16),
            HorizontalAlignment = HorizontalAlignment.Right,
            IsDefault = true, IsCancel = true
        };
        if (darkButtonStyle is not null) okButton.Style = darkButtonStyle;
        okButton.Click += (_, _) => dialog.DialogResult = true;

        var root = new Border { Background = bgBrush };
        var mainPanel = new StackPanel();
        mainPanel.Children.Add(contentPanel);
        mainPanel.Children.Add(okButton);
        root.Child = mainPanel;
        dialog.Content = root;
        dialog.ShowDialog();
    }

    internal static MessageBoxResult AskSaveChanges() =>
        ShowThemedMessageBox(
            Application.Current.MainWindow,
            "현재 프로젝트에 저장되지 않은 변경이 있습니다.\n저장하시겠습니까?",
            "저장 확인",
            MessageBoxButton.YesNoCancel,
            "?");

    internal static MessageBoxResult ShowThemedMessageBox(
        string message, string title, MessageBoxButton buttons, string icon) =>
        ShowThemedMessageBox(Application.Current.MainWindow, message, title, buttons, icon, showDontShowAgain: false, out _);

    internal static MessageBoxResult ShowThemedMessageBox(
        Window? owner, string message, string title, MessageBoxButton buttons, string icon) =>
        ShowThemedMessageBox(owner, message, title, buttons, icon, showDontShowAgain: false, out _);

    internal static MessageBoxResult ShowThemedMessageBox(
        string message, string title, MessageBoxButton buttons, string icon,
        bool showDontShowAgain, out bool dontShowAgain) =>
        ShowThemedMessageBox(Application.Current.MainWindow, message, title, buttons, icon, showDontShowAgain, out dontShowAgain);

    /// <summary>"다시 보지 않기" 체크박스 포함 오버로드</summary>
    internal static MessageBoxResult ShowThemedMessageBox(
        Window? owner, string message, string title, MessageBoxButton buttons, string icon,
        bool showDontShowAgain, out bool dontShowAgain)
    {
        var result = MessageBoxResult.None;
        dontShowAgain = false;

        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner ?? Application.Current.MainWindow,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false
        };

        var darkButtonStyle = Application.Current.TryFindResource("DarkButton") as Style;

        var iconBlock = new TextBlock
        {
            Text = icon,
            FontSize = 28,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 12, 0)
        };

        var messageBlock = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13
        };

        var contentPanel = new Grid { Margin = new Thickness(16, 16, 16, 12) };
        contentPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        contentPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(iconBlock, 0);
        Grid.SetColumn(messageBlock, 1);
        contentPanel.Children.Add(iconBlock);
        contentPanel.Children.Add(messageBlock);

        CheckBox? dontShowCheck = null;
        if (showDontShowAgain)
        {
            dontShowCheck = new CheckBox
            {
                Content = "다음 시뮬레이션까지 다시 보지 않기",
                Foreground = (System.Windows.Media.Brush?)Application.Current.TryFindResource("SecondaryTextBrush")
                             ?? System.Windows.SystemColors.ControlTextBrush,
                FontSize = 12,
                Margin = new Thickness(16, 0, 16, 10)
            };
        }

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 16, 16)
        };

        Button MakeButton(string content, MessageBoxResult value, bool isDefault = false, bool isCancel = false)
        {
            var btn = new Button
            {
                Content = content,
                MinWidth = 70,
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(4, 0, 0, 0),
                IsDefault = isDefault,
                IsCancel = isCancel
            };
            if (darkButtonStyle is not null) btn.Style = darkButtonStyle;
            btn.Click += (_, _) => { result = value; dialog.DialogResult = true; };
            return btn;
        }

        switch (buttons)
        {
            case MessageBoxButton.YesNoCancel:
                buttonPanel.Children.Add(MakeButton("예(Y)", MessageBoxResult.Yes, isDefault: true));
                buttonPanel.Children.Add(MakeButton("아니요(N)", MessageBoxResult.No));
                buttonPanel.Children.Add(MakeButton("취소", MessageBoxResult.Cancel, isCancel: true));
                break;
            case MessageBoxButton.YesNo:
                buttonPanel.Children.Add(MakeButton("예(Y)", MessageBoxResult.Yes, isDefault: true));
                buttonPanel.Children.Add(MakeButton("아니요(N)", MessageBoxResult.No, isCancel: true));
                break;
            case MessageBoxButton.OKCancel:
                buttonPanel.Children.Add(MakeButton("확인", MessageBoxResult.OK, isDefault: true));
                buttonPanel.Children.Add(MakeButton("취소", MessageBoxResult.Cancel, isCancel: true));
                break;
            default:
                buttonPanel.Children.Add(MakeButton("확인", MessageBoxResult.OK, isDefault: true, isCancel: true));
                break;
        }

        var root = new Border
        {
            Background = (System.Windows.Media.Brush?)Application.Current.TryFindResource("SecondaryBackgroundBrush")
                         ?? System.Windows.SystemColors.WindowBrush
        };
        var mainPanel = new StackPanel();
        mainPanel.Children.Add(contentPanel);
        if (dontShowCheck is not null)
            mainPanel.Children.Add(dontShowCheck);
        mainPanel.Children.Add(buttonPanel);
        root.Child = mainPanel;
        dialog.Content = root;

        if (dialog.ShowDialog() != true && result == MessageBoxResult.None)
            result = buttons == MessageBoxButton.OK ? MessageBoxResult.OK : MessageBoxResult.Cancel;

        if (dontShowCheck is not null)
            dontShowAgain = dontShowCheck.IsChecked == true;

        return result;
    }

    /// <summary>
    /// 시뮬레이션 중 편집 차단 경고 + "시뮬레이션 종료" 옵션 다이얼로그.
    /// "확인" 은 default 이며 거부(false), "시뮬레이션 종료" 는 명시적 클릭 시 true.
    /// </summary>
    /// <returns>사용자가 시뮬 종료를 선택했으면 true, 그 외 false</returns>
    // TODO: 향후 동일한 커스텀 버튼 라벨 다이얼로그가 한 번 더 필요해지면
    //       ShowThemedMessageBox에 (string label, MessageBoxResult value)[] 오버로드를 추가하여 통합할 것.
    internal static bool ShowSimulationStopOptionDialog(string message)
    {
        var owner = Application.Current.MainWindow;
        var dialog = new Window
        {
            Title = "시뮬레이션 중 편집 차단",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false
        };

        var darkButtonStyle = Application.Current.TryFindResource("DarkButton") as Style;

        var iconBlock = new TextBlock
        {
            Text = IconWarn,
            FontSize = 28,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 12, 0)
        };

        var messageBlock = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13
        };

        var contentPanel = new Grid { Margin = new Thickness(16, 16, 16, 12) };
        contentPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        contentPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(iconBlock, 0);
        Grid.SetColumn(messageBlock, 1);
        contentPanel.Children.Add(iconBlock);
        contentPanel.Children.Add(messageBlock);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 16, 16)
        };

        var stopChosen = false;

        Button MakeButton(string content, bool isDefault, bool isCancel, Action onClick)
        {
            var btn = new Button
            {
                Content = content,
                MinWidth = 110,
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(4, 0, 0, 0),
                IsDefault = isDefault,
                IsCancel = isCancel
            };
            if (darkButtonStyle is not null) btn.Style = darkButtonStyle;
            btn.Click += (_, _) => { onClick(); dialog.DialogResult = true; };
            return btn;
        }

        // 의도치 않은 mutation 방지를 위해 default(Enter)는 "확인"
        buttonPanel.Children.Add(MakeButton("확인", isDefault: true, isCancel: true, () => stopChosen = false));
        buttonPanel.Children.Add(MakeButton("시뮬레이션 종료", isDefault: false, isCancel: false, () => stopChosen = true));

        var root = new Border
        {
            Background = (Brush?)Application.Current.TryFindResource("SecondaryBackgroundBrush")
                         ?? SystemColors.WindowBrush
        };
        var mainPanel = new StackPanel();
        mainPanel.Children.Add(contentPanel);
        mainPanel.Children.Add(buttonPanel);
        root.Child = mainPanel;
        dialog.Content = root;

        dialog.ShowDialog();
        return stopChosen;
    }
}
