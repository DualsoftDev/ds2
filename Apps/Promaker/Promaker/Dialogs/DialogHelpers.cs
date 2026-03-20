using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Promaker.Dialogs;

internal static class DialogHelpers
{
    internal static bool ShowOwnedDialog(Window dialog)
    {
        dialog.Owner = Application.Current.MainWindow;
        dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        return dialog.ShowDialog() == true;
    }

    internal static void Warn(string message) =>
        ShowThemedMessageBox(message, "경고", MessageBoxButton.OK, "⚠");

    internal static void Info(string message) =>
        ShowThemedMessageBox(message, "알림", MessageBoxButton.OK, "ℹ");

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

    internal static Guid? PickCallFromList(string title, IReadOnlyList<(Guid Id, string Name)> calls,
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
        return null;
    }

    internal static MessageBoxResult AskSaveChanges() =>
        ShowThemedMessageBox(
            "현재 프로젝트에 저장되지 않은 변경이 있습니다.\n저장하시겠습니까?",
            "저장 확인",
            MessageBoxButton.YesNoCancel,
            "?");

    internal static MessageBoxResult ShowThemedMessageBox(
        string message, string title, MessageBoxButton buttons, string icon) =>
        ShowThemedMessageBox(message, title, buttons, icon, showDontShowAgain: false, out _);

    /// <summary>"다시 보지 않기" 체크박스 포함 오버로드</summary>
    internal static MessageBoxResult ShowThemedMessageBox(
        string message, string title, MessageBoxButton buttons, string icon,
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
            Owner = Application.Current.MainWindow,
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
}
