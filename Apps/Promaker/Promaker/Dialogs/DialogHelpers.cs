using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Promaker.Dialogs;

internal static class DialogHelpers
{
    internal static void Warn(string message) =>
        MessageBox.Show(message, "입력 오류", MessageBoxButton.OK, MessageBoxImage.Warning);

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

    /// <summary>
    /// 저장되지 않은 변경이 있을 때 확인 팝업.
    /// Yes=저장 후 계속, No=저장 안 하고 계속, Cancel=취소.
    /// </summary>
    internal static MessageBoxResult AskSaveChanges() =>
        MessageBox.Show(
            "현재 프로젝트에 저장되지 않은 변경이 있습니다.\n저장하시겠습니까?",
            "저장 확인",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
}
