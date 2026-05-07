using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Promaker.Behaviors;

/// <summary>
/// Ctrl + Mouse Wheel 로 LayoutTransform(ScaleTransform) 의 Scale 을 조절하는 attached behavior.
///
/// 사용 예 (XAML):
///   xmlns:b="clr-namespace:Promaker.Behaviors"
///   ...
///   &lt;Grid b:CtrlWheelZoom.IsEnabled="True" ...&gt;
///
/// EditorCanvas 의 ZoomTransform 패턴 (Controls/Canvas/EditorCanvas.Navigation.cs:OnMouseWheel) 의
/// 일반화 — Ctrl 모디파이어 게이트를 추가해 스크롤 가능한 컨테이너 (ListBox / ScrollViewer) 에서도
/// 평소 wheel scroll 을 보존한다.
///
/// 적용 element 의 LayoutTransform 이 ScaleTransform 이 아니면 ScaleTransform 으로 교체. 이미
/// ScaleTransform 이면 재사용 (Scale 누적 X — 항상 절대값 설정).
/// 부모 컨테이너가 layout space 를 fixed 로 제약하는 경우 (e.g. dock column) Scale &gt; 1 시
/// content 가 horizontal 로 overflow 되어 clipped 표시될 수 있음 — 정상 LayoutTransform 동작.
/// </summary>
public static class CtrlWheelZoom
{
    /// <summary>
    /// 활성화 시 PreviewMouseWheel 을 등록한다. PreviewMouseWheel 은 tunnel 단계라 자식의 wheel
    /// 핸들러보다 먼저 호출되며, Ctrl 모디파이어 일 때만 Handled=true 로 zoom 처리하고 그 외에는
    /// pass-through 하여 ListBox/ScrollViewer 의 기본 스크롤이 동작한다.
    /// </summary>
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(CtrlWheelZoom),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    private const double Min = 0.5;
    private const double Max = 3.0;
    private const double Step = 0.1;

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        if ((bool)e.NewValue)
            fe.PreviewMouseWheel += OnPreviewMouseWheel;
        else
            fe.PreviewMouseWheel -= OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;
        if (sender is not FrameworkElement fe) return;

        if (fe.LayoutTransform is not ScaleTransform st)
        {
            st = new ScaleTransform(1.0, 1.0);
            fe.LayoutTransform = st;
        }

        var step = e.Delta > 0 ? Step : -Step;
        var next = Math.Clamp(Math.Round(st.ScaleX + step, 2), Min, Max);
        if (Math.Abs(next - st.ScaleX) < 0.0001) return;
        st.ScaleX = next;
        st.ScaleY = next;
        e.Handled = true;
    }
}
