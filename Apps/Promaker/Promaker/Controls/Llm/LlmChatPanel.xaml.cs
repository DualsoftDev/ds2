using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Promaker.ViewModels;

namespace Promaker.Controls.Llm;

/// <summary>
/// MainWindow 안 dock LLM Chat panel (1d-4 D — 별도 Window 폐기 후 이전).
/// DataContext 는 외부 (`MainViewModel.LlmChatVm`) 에서 주입.
///
/// commit-4 (2026-05-09): drag-drop 파일 첨부 진입점 추가.
///   <see cref="Panel_PreviewDragOver"/> / <see cref="Panel_PreviewDrop"/> 가 <c>e.Handled=true</c> 처리해
///   부모 MainWindow 의 <c>Window_Drop</c> (단일 파일 import) bubble 차단 — 정책 14.
/// </summary>
public partial class LlmChatPanel : UserControl
{
    public LlmChatPanel()
    {
        InitializeComponent();
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
            tb.SelectedText = Environment.NewLine;
            tb.SelectionStart += Environment.NewLine.Length;
            tb.SelectionLength = 0;
            e.Handled = true;
        }
    }

    /// <summary>
    /// 정책 14 강화 (commit-4 검열 M1): MainWindow 의 <c>Window_DragEnter</c> 가 bubble 단계에서
    /// <c>FileDragOverlay.Visibility=Visible</c> 로 LLM Chat 패널을 가리지 않도록 PreviewDragEnter/Leave/Over/Drop
    /// 4종 모두 차단. <c>e.Handled=true</c> 만으로 bubble 중단.
    /// </summary>
    private void Panel_PreviewDragEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Panel_PreviewDragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
    }

    /// <summary>
    /// 정책 14: PreviewDragOver 단계에서 흡수해 부모 MainWindow 의 <c>Window_DragOver</c> 가 단일 파일 import 로
    /// 처리하는 bubble 자체를 차단. FileDrop 만 Copy effect 로 허용.
    /// </summary>
    private void Panel_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// 정책 14 + 18: STA sync 단계에서 paths 추출 후 ViewModel 의 <see cref="LlmChatViewModel.AddPathsAsync"/> 위임.
    /// AddPathsAsync 가 자체적으로 background bytes 로드 + dispatcher chip 추가. 본 handler 는 fire-and-forget
    /// 이지만 AddPathsAsync 는 unobserved exception 안 던지도록 내부에서 처리.
    /// </summary>
    private void Panel_PreviewDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not LlmChatViewModel vm) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        // fire-and-forget — UI thread 블록하지 않음. AddPathsAsync 는 내부에서 background marshalling 처리.
        _ = vm.AddPathsAsync(paths);
    }
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}
