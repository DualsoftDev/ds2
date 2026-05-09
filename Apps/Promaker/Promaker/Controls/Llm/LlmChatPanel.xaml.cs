using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;
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
        // commit-5 (정책 2 정석): DataObject.AddPastingHandler 로 Ctrl+V 가로채기. text-only paste 는 회귀 보장.
        DataObject.AddPastingHandler(InputBox, OnInputPaste);
    }

    /// <summary>
    /// Ctrl+V 진입점. 우선순위 (정책 3.3절): file drop list > CF_PNG raw > BitmapSource fallback > text.
    /// text-only 이면 cancel 안 함 → 기존 TextBox paste 동작 유지 (MI-3).
    /// commit-6 m1: BitmapSource fallback 의 PngBitmapEncoder 인코딩이 큰 이미지 (4K+) 에서 STA freeze
    /// 가능 → background <see cref="Task.Run"/> 분리 + UI thread continuation 으로 chip 추가.
    /// </summary>
    private void OnInputPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (DataContext is not LlmChatViewModel vm) return;
        var data = e.SourceDataObject;

        // ① 파일 drop 리스트
        if (data.GetDataPresent(DataFormats.FileDrop))
        {
            if (data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
            {
                e.CancelCommand();
                _ = vm.AddPathsAsync(paths);
                return;
            }
        }

        // ② CF_PNG raw — STA 즉시 처리 (재인코딩 회피, ToArray 만 — UI thread 부하 ms 단위)
        if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream rawPng)
        {
            var bytes = rawPng.ToArray();
            if (bytes.Length > 0)
            {
                e.CancelCommand();
                _ = vm.AddImageBytesAsync(bytes, "image/png", PasteFileName());
                return;
            }
        }

        // ③ BitmapSource fallback — STA 단계 Freeze 만, background 인코딩 (m1 — 4K 이미지 etc UI freeze 회피).
        if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource src)
        {
            BitmapSource frozen;
            if (src.IsFrozen) frozen = src;
            else
            {
                frozen = src.Clone();
                if (frozen.CanFreeze) frozen.Freeze();
            }
            e.CancelCommand();
            var name = PasteFileName();
            // TaskScheduler.FromCurrentSynchronizationContext() = paste handler 의 UI thread 캡처.
            // continuation 이 UI thread 에서 vm.AddImageBytesAsync 호출 → ObservableCollection cross-thread access 회피.
            var uiScheduler = TaskScheduler.FromCurrentSynchronizationContext();
            _ = Task.Run(() => EncodeBitmapToPng(frozen))
                .ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully && t.Result is { Length: > 0 } bytes)
                        _ = vm.AddImageBytesAsync(bytes, "image/png", name);
                }, uiScheduler);
            return;
        }

        // ④ text 만 → 기존 paste 동작 유지 (cancel 안 함).
    }

    private static string PasteFileName() => $"clipboard-{DateTime.Now:HHmmss}.png";

    /// <summary>
    /// background thread 에서 frozen BitmapSource → PNG bytes. PngBitmapEncoder 가 frozen ref 만 참조해 race-free.
    /// </summary>
    private static byte[] EncodeBitmapToPng(BitmapSource frozen)
    {
        using var ms = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(frozen));
        encoder.Save(ms);
        return ms.ToArray();
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
    /// commit-5: FileDrop 가 들어왔을 때 <see cref="DragHighlightBorder"/> 의 BorderBrush 를 accent 로 토글.
    /// </summary>
    private void Panel_PreviewDragEnter(object sender, DragEventArgs e)
    {
        var ok = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        if (ok) DragHighlightBorder.BorderBrush = TryFindResource("AccentBrush") as System.Windows.Media.Brush
                                                  ?? System.Windows.Media.Brushes.DodgerBlue;
        e.Handled = true;
    }

    private void Panel_PreviewDragLeave(object sender, DragEventArgs e)
    {
        DragHighlightBorder.BorderBrush = System.Windows.Media.Brushes.Transparent;
        e.Handled = true;
    }

    /// <summary>
    /// 정책 14: PreviewDragOver 단계에서 흡수해 부모 MainWindow 의 <c>Window_DragOver</c> 가 단일 파일 import 로
    /// 처리하는 bubble 자체를 차단. FileDrop 만 Copy effect 로 허용.
    /// commit-5 검열 M1: DragEnter/Over 짝 맞추기 — DragOver 에서도 BorderBrush 재토글 (FileDrop 이 아닌 데이터로
    /// enter 후 FileDrop 으로 swap 되는 edge case 에서 highlight 누락 방지).
    /// </summary>
    private void Panel_PreviewDragOver(object sender, DragEventArgs e)
    {
        var ok = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DragHighlightBorder.BorderBrush = ok
            ? (TryFindResource("AccentBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.DodgerBlue)
            : System.Windows.Media.Brushes.Transparent;
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
        DragHighlightBorder.BorderBrush = System.Windows.Media.Brushes.Transparent;
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
