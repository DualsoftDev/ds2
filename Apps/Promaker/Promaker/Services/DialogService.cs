using System;
using System.Windows;
using Microsoft.Win32;

namespace Promaker.Services;

/// <summary>
/// 다이얼로그 표시를 담당하는 서비스 구현
/// </summary>
public class DialogService : IDialogService
{
    public string? PromptName(string title, string defaultName)
    {
        return Dialogs.DialogHelpers.PromptName(title, defaultName);
    }

    public bool Confirm(string message, string title)
        => Dialogs.DialogHelpers.Confirm(Application.Current.MainWindow, message, title);

    public void ShowWarning(string message)
    {
        Dialogs.DialogHelpers.Warn(message);
    }

    public void ShowError(string message)
        => Dialogs.DialogHelpers.Error(Application.Current.MainWindow, message);

    public void ShowInfo(string message)
    {
        Dialogs.DialogHelpers.Info(message);
    }

    public MessageBoxResult AskSaveChanges()
    {
        return Dialogs.DialogHelpers.AskSaveChanges();
    }

    public string? ShowOpenFileDialog(string filter)
    {
        var dialog = new OpenFileDialog
        {
            Filter = filter
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? ShowSaveFileDialog(string filter, string? defaultFileName = null)
    {
        var dialog = new SaveFileDialog
        {
            Filter = filter,
            DefaultExt = ".sdf"
        };

        if (!string.IsNullOrWhiteSpace(defaultFileName))
            dialog.FileName = defaultFileName;

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public T? ShowDialog<T>(Window dialog) where T : class
    {
        if (Application.Current.MainWindow is { } owner)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        return dialog.ShowDialog() == true ? dialog.DataContext as T : null;
    }

    public bool? ShowDialog(Window dialog)
    {
        if (Application.Current.MainWindow is { } owner)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        return dialog.ShowDialog();
    }
}
