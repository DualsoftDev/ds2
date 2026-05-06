using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using Ds2.Core.Store;
using Promaker.ViewModels;

namespace Promaker.Windows;

public partial class LlmChatWindow : Window
{
    public LlmChatWindow(DsStore store)
    {
        InitializeComponent();
        DataContext = new LlmChatViewModel(store);
        Closed += async (_, _) =>
        {
            if (DataContext is LlmChatViewModel vm)
                await vm.DisposeAsync();
        };
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+Enter 로 전송, 단순 Enter 는 줄바꿈
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (DataContext is LlmChatViewModel vm && vm.SendCommand.CanExecute(null))
            {
                vm.SendCommand.Execute(null);
                e.Handled = true;
            }
        }
    }
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}
