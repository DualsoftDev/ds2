using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Ds2.Core;
using Ds2.Editor;
using Promaker.Presentation;
using Promaker.ViewModels;

namespace Promaker.Controls;

public partial class MainToolbar : UserControl
{
    /// 리본 폭이 모자라 우측 버튼이 잘리는 상태. true 면 Etc 섹션이 '⋯' 드롭다운으로 접힌다.
    /// 저해상도에서 우측 버튼이 화면 밖으로 밀려 아예 누를 수 없던 문제(#ribbon-overflow) 대응.
    public static readonly DependencyProperty IsCompactProperty =
        DependencyProperty.Register(nameof(IsCompact), typeof(bool), typeof(MainToolbar),
            new PropertyMetadata(false));

    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        private set => SetValue(IsCompactProperty, value);
    }

    /// compact 해제 시 요구하는 여유 폭(px). 없으면 경계에서 왕복한다.
    private const double CompactHysteresisPx = 48.0;

    /// 펼친 상태에서 측정한 요구 폭. compact 로 접히면 폭이 줄어드는데 그 값으로 다시 비교하면
    /// 곧바로 펼쳐지고 다시 접히는 진동이 생기므로, 판정 기준은 펼친 상태 값으로 고정한다.
    private double _expandedNeededWidth;

    public MainToolbar()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            InitializeConnectPinStates();
            UpdateCompactState();
        };
        // SizeChanged 만 구독한다. LayoutUpdated 는 트리 전체 레이아웃마다 돌아 CPU 를 먹고,
        // 여기서 DP 를 바꾸면 레이아웃을 다시 유발해 진동 위험이 있다.
        SizeChanged += (_, _) => UpdateCompactState();
    }

    /// 자식 섹션의 DesiredSize 합(= Auto 열이라 무한 폭 기준의 '원하는 폭')과 실제 폭을 비교해
    /// 넘치면 compact 로 전환한다. 임계값을 상수로 박지 않고 실측으로 판정한다.
    private void UpdateCompactState()
    {
        if (RibbonGrid is null || ActualWidth <= 0)
            return;

        if (!IsCompact)
        {
            double needed = 0;
            foreach (UIElement child in RibbonGrid.Children)
            {
                if (child.Visibility != Visibility.Visible)
                    continue;
                needed += child.DesiredSize.Width;
            }
            if (needed > 0)
                _expandedNeededWidth = needed;
        }
        if (_expandedNeededWidth <= 0)
            return;

        var available = ActualWidth;
        var next = IsCompact
            ? available < _expandedNeededWidth + CompactHysteresisPx   // 펼치려면 여유가 더 있어야 한다
            : available < _expandedNeededWidth;
        if (next != IsCompact)
            IsCompact = next;
    }

    private MainViewModel? VM => DataContext as MainViewModel;

    private void CloseSavePopup(object sender, RoutedEventArgs e) => SaveMenuToggle.IsChecked = false;
    private void CloseUploadPopup(object sender, RoutedEventArgs e) => UploadMenuToggle.IsChecked = false;
    private void CloseOpenPopup(object sender, RoutedEventArgs e) => OpenMenuToggle.IsChecked = false;
    private void CloseEditPopup(object sender, RoutedEventArgs e) => EditMenuToggle.IsChecked = false;

    private void ConnectType_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && VM is { } vm)
        {
            vm.SelectedConnectArrowType = tag switch
            {
                "Reset" => ArrowType.Reset,
                "StartReset" => ArrowType.StartReset,
                "ResetReset" => ArrowType.ResetReset,
                "Group" => ArrowType.Group,
                _ => ArrowType.Start
            };
            ConnectTypeToggle.IsChecked = false;
        }
    }

    private void ConnectTypePopup_Opened(object sender, EventArgs e)
    {
        if (VM is not { } vm) return;

        var isWorkMode = vm.Canvas.ActiveTab is { } tab
            && EntityKindRules.isWorkArrowModeForTab(tab.Kind);

        var vis = isWorkMode ? Visibility.Visible : Visibility.Collapsed;
        ConnResetRadio.Visibility = vis;
        ConnStartResetRadio.Visibility = vis;
        ConnResetResetRadio.Visibility = vis;

        // Call 모드에서 Work 전용 타입이 선택돼 있으면 Start로 폴백
        if (!isWorkMode && vm.SelectedConnectArrowType is ArrowType.Reset or ArrowType.StartReset or ArrowType.ResetReset)
            vm.SelectedConnectArrowType = ArrowType.Start;

        var radio = vm.SelectedConnectArrowType switch
        {
            ArrowType.Reset => ConnResetRadio,
            ArrowType.StartReset => ConnStartResetRadio,
            ArrowType.ResetReset => ConnResetResetRadio,
            ArrowType.Group => ConnGroupRadio,
            _ => ConnStartRadio
        };
        radio.IsChecked = true;
    }

    private void ConnectPin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tagStr }
            && Enum.TryParse<ArrowType>(tagStr, out var type))
        {
            ArrowTypeFrequencyTracker.TogglePin(type);
        }
    }

    private void InitializeConnectPinStates()
    {
        ConnStartPin.IsChecked = ArrowTypeFrequencyTracker.IsPinned(ArrowType.Start);
        ConnResetPin.IsChecked = ArrowTypeFrequencyTracker.IsPinned(ArrowType.Reset);
        ConnStartResetPin.IsChecked = ArrowTypeFrequencyTracker.IsPinned(ArrowType.StartReset);
        ConnResetResetPin.IsChecked = ArrowTypeFrequencyTracker.IsPinned(ArrowType.ResetReset);
        ConnGroupPin.IsChecked = ArrowTypeFrequencyTracker.IsPinned(ArrowType.Group);
    }
}
