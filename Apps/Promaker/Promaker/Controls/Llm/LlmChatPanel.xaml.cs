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

        // ② CF_PNG raw — WPF가 byte[] / Stream / MemoryStream 등 형식 가변적으로 반환.
        //    Snipping Tool / Greenshot / Slack / Discord 등 도구별 clipboard 형식 차이를 모두 흡수.
        if (data.GetDataPresent("PNG"))
        {
            var pngBytes = ExtractBytes(data.GetData("PNG"));
            if (pngBytes is { Length: > 0 })
            {
                e.CancelCommand();
                _ = vm.AddImageBytesAsync(pngBytes, "image/png", PasteFileName());
                return;
            }
        }

        // ③ BitmapSource fallback — paste handler 의 IDataObject 가 CF_BITMAP 미노출하더라도
        //    Clipboard.ContainsImage() 가 CF_DIB / CF_DIBV5 / Format17 등을 통합 처리해 BitmapSource 반환.
        //    (브라우저 우클릭 "이미지 복사" 등은 CF_BITMAP 없이 CF_DIB 만 노출하는 경우 다수)
        //    PreviewKeyDown 의 Ctrl+V 가로채기 (TryHandleClipboardPaste) 가 통상 우선 발화하므로 본 분기는
        //    대부분 dead path 이지만, IME 조합 / 컨텍스트 메뉴 paste / 외부 RoutedCommand 발화 등 edge
        //    case 대비 방어적 잔존.
        BitmapSource? src = data.GetData(DataFormats.Bitmap) as BitmapSource;
        if (src is null
            && (data.GetDataPresent(DataFormats.Bitmap)
                || data.GetDataPresent("DeviceIndependentBitmap")
                || data.GetDataPresent("Format17"))
            && Clipboard.ContainsImage())
        {
            try { src = Clipboard.GetImage(); }
            catch { /* clipboard 동시성 race — 다음 paste 시도 */ }
        }
        if (src is not null)
        {
            e.CancelCommand();
            EnqueueBitmapImage(vm, src);
            return;
        }

        // ④ text 만 → 기존 paste 동작 유지 (cancel 안 함).
    }

    /// <summary>
    /// BitmapSource → PNG bytes background 인코딩 + UI thread continuation 으로 chip 추가.
    /// <see cref="OnInputPaste"/> ③번 분기 + <see cref="TryHandleClipboardPaste"/> ② 분기 공통 helper (Minor-1).
    /// 호출자는 STA thread (paste handler / PreviewKeyDown) 가정 — TaskScheduler.FromCurrentSynchronizationContext 캡처.
    /// </summary>
    private static void EnqueueBitmapImage(LlmChatViewModel vm, BitmapSource src)
    {
        BitmapSource frozen;
        if (src.IsFrozen) frozen = src;
        else
        {
            frozen = src.Clone();
            if (frozen.CanFreeze) frozen.Freeze();
        }
        var name = PasteFileName();
        var uiScheduler = TaskScheduler.FromCurrentSynchronizationContext();
        _ = Task.Run(() => EncodeBitmapToPng(frozen))
            .ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully && t.Result is { Length: > 0 } bytes)
                    _ = vm.AddImageBytesAsync(bytes, "image/png", name);
            }, uiScheduler);
    }

    /// <summary>
    /// CF_PNG 등 Stream / byte[] / MemoryStream 형식을 모두 흡수해 byte[] 반환. null/empty 면 null.
    /// WPF 의 <c>IDataObject.GetData</c> 가 형식별로 다른 타입 반환 (도구마다 차이) 흡수용.
    /// 비-MemoryStream 분기는 사실상 dead path (paste 경로의 Stream 은 거의 MemoryStream) — 그러나
    /// IDataObject 의 Stream ownership 정책이 모호해 leak 회피 위해 명시적 dispose.
    /// </summary>
    private static byte[]? ExtractBytes(object? raw)
    {
        switch (raw)
        {
            case byte[] b: return b.Length > 0 ? b : null;
            case MemoryStream ms: return ms.ToArray();
            case Stream s:
                using (s)
                using (var copy = new MemoryStream())
                {
                    s.CopyTo(copy);
                    return copy.ToArray();
                }
            default: return null;
        }
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
    ///
    /// commit-6b 후속 (Ctrl+V 회귀 fix): WPF TextBox 의 ApplicationCommands.Paste CanExecute 가
    /// 클립보드에 텍스트 부재 시 false → OnInputPaste 자체 미발화. 따라서 image only / file drop only
    /// 클립보드는 PreviewKeyDown 에서 직접 가로채야 함 (텍스트 paste 는 default 흐름 보존).
    /// </summary>
    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;
        var actual = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;

        if (actual == Key.V && mods == ModifierKeys.Control)
        {
            // 텍스트 외 클립보드 (이미지 / 파일 drop) 만 가로채. 텍스트는 default paste 가 처리.
            if (TryHandleClipboardPaste())
                e.Handled = true;
            return;
        }

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
    /// commit-6b 후속: Clipboard API 로 직접 검사 — file drop list > image > 그 외(false → default text paste).
    /// 우선순위는 정책 3.3 (`Ctrl+V 우선순위: file list > image > text`) 과 일치 — **첫 매칭 형식만 처리**가 의도.
    /// 텍스트 + 이미지 mixed 클립보드는 정책상 이미지가 우선되며 텍스트는 무시됨 (사용자가 text 만 paste 하려면
    /// 텍스트만 클립보드에 두고 다시 시도).
    /// </summary>
    private bool TryHandleClipboardPaste()
    {
        if (DataContext is not LlmChatViewModel vm) return false;

        // ① 파일 drop 리스트 (탐색기 Ctrl+C → input Ctrl+V)
        if (Clipboard.ContainsFileDropList())
        {
            var list = Clipboard.GetFileDropList();
            if (list.Count > 0)
            {
                var arr = new string[list.Count];
                list.CopyTo(arr, 0);
                _ = vm.AddPathsAsync(arr);
                return true;
            }
        }

        // ② 이미지 — Clipboard.ContainsImage 가 CF_BITMAP / CF_DIB / CF_DIBV5 등을 통합 처리.
        if (Clipboard.ContainsImage())
        {
            BitmapSource? src;
            try { src = Clipboard.GetImage(); }
            catch { return false; }
            if (src is null) return false;
            EnqueueBitmapImage(vm, src);
            return true;
        }

        // ③ 텍스트만 → default paste 가 처리 (e.Handled=false)
        return false;
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
