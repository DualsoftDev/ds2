using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
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

    /// <summary>
    /// Enter (modifier 없음) = 전송. Shift+Enter = 줄바꿈 (TextBox 기본 동작). Alt+J = 줄바꿈 삽입.
    /// PreviewKeyDown 으로 후킹해 TextBox 가 Enter 를 줄바꿈으로 처리하기 전에 가로챔.
    /// Alt 조합은 WPF 가 Key.System 으로 마샬링하므로 SystemKey 로 분기.
    /// </summary>
    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;
        var actual = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;

        if (actual == Key.Enter && mods == ModifierKeys.None)
        {
            if (DataContext is LlmChatViewModel vm && vm.SendCommand.CanExecute(null))
                vm.SendCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (actual == Key.J && mods == ModifierKeys.Alt)
        {
            // SelectedText 가 selection 치환 + binding round-trip 처리해 줌. 직접 Text 재할당 회피.
            tb.SelectedText = Environment.NewLine;
            tb.SelectionStart += Environment.NewLine.Length;
            tb.SelectionLength = 0;
            e.Handled = true;
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
