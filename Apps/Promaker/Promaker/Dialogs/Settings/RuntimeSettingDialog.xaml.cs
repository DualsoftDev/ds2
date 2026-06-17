using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Ds2.Core;
using Promaker.ViewModels;

namespace Promaker.Windows;

public partial class RuntimeSettingDialog : Window
{
    private static readonly log4net.ILog Log =
        log4net.LogManager.GetLogger("Runtime");

    private const string VariantKey = "C";
    private readonly MainViewModel _vm;
    private List<ModeItemVM> _items = new();
    private bool _syncingSelection;   // ComboBox ↔ 카드 선택 동기화 중 재진입 방지

    public RuntimeSettingDialog(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm.Simulation;   // HubAddress · NeedsHubConnection 양방향 바인딩

        _items = BuildItems(vm.Simulation.SelectedRuntimeMode);
        ModeList.ItemsSource = _items;
        ModeCombo.ItemsSource = _items;                                  // 상단 드롭다운도 같은 선택 상태 공유
        ModeCombo.SelectedItem = _items.FirstOrDefault(v => v.IsSelected);
        RefreshThumbnails();

        // 현재 VM 의 실 PLC 연결 토글 반영 후, 선택 모드에 맞춰 활성/푸터 상태 갱신.
        RealPlcCheckBox.IsChecked = vm.Simulation.IsRealPlcConnected;
        UpdateModeDependentState();
    }

    /// <summary>ComboBox 또는 카드 클릭으로 모드를 선택 — 두 표시를 동기화하고 모드 의존 상태를 갱신.</summary>
    private void SelectMode(ModeItemVM item)
    {
        foreach (var vm in _items)
            vm.IsSelected = ReferenceEquals(vm, item);

        _syncingSelection = true;
        try { ModeCombo.SelectedItem = item; }
        finally { _syncingSelection = false; }

        UpdateModeDependentState();
    }

    private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection) return;
        if (ModeCombo.SelectedItem is ModeItemVM item)
            SelectMode(item);
    }

    /// <summary>선택 모드에 따라 Hub 주소 / 실 PLC 옵션 활성 여부와 하단 PLC 상태 표시를 갱신.</summary>
    private void UpdateModeDependentState()
    {
        var selected = _items.FirstOrDefault(v => v.IsSelected);
        // Sim 모드는 Hub 연결 불필요 → 주소 편집 불가.
        var isSim = selected is null || selected.Mode == RuntimeMode.Simulation;
        // 실 PLC 옵션은 Control/Monitoring 에서만 의미 (둘 다 실 PLC 직접 연결).
        var requiresPlc = selected is not null
            && (selected.Mode == RuntimeMode.Control || selected.Mode == RuntimeMode.Monitoring);

        HubAddressBox.IsEnabled = !isSim;
        RealPlcCheckBox.IsEnabled = requiresPlc;
        PlcSettingsButton.IsEnabled = requiresPlc && RealPlcCheckBox.IsChecked == true;

        UpdatePlcFooter();
    }

    private void RealPlcCheckBox_Toggled(object sender, RoutedEventArgs e)
    {
        // 체크박스 상태 → 설정 버튼 활성화 (실 PLC 가 의미 있는 모드에서만)
        PlcSettingsButton.IsEnabled = RealPlcCheckBox.IsEnabled && RealPlcCheckBox.IsChecked == true;
        UpdatePlcFooter();
    }

    private void PlcSettings_Click(object sender, RoutedEventArgs e)
    {
        // IO 매핑이 비어 있으면 사용자에게 즉시 알려준다 — 다이얼로그 안에서도 안내.
        var tagCount = _vm.Simulation.CountAutoImportablePlcAddresses();
        var dialog = new PlcSettingsDialog(_vm.Simulation.PlcSettings, tagCount, _vm.Simulation.AutoDurationCalibrate)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            // 자동 정합 토글 결과 적용 — set 시 OnChanged 가 hub 로 전파(전 인스턴스 동기 + 엔진 적용).
            _vm.Simulation.AutoDurationCalibrate = dialog.AutoDurationCalibrate;
            // 간트 표시 윈도우 결과 적용 — set 시 OnChanged 가 GanttChart.RenderWindowMinutes 갱신.
            _vm.Simulation.GanttWindowMinutes = dialog.GanttWindowMinutes;
            UpdatePlcFooter();
        }
    }

    /// <summary>하단 푸터의 PLC 연결 상태(점 색 + 텍스트)를 현재 모드/체크박스/PlcSettings 로 갱신.</summary>
    private void UpdatePlcFooter()
    {
        var selected = _items.FirstOrDefault(v => v.IsSelected);
        var requiresPlc = selected is not null
            && (selected.Mode == RuntimeMode.Control || selected.Mode == RuntimeMode.Monitoring);
        var enabled = requiresPlc && RealPlcCheckBox.IsChecked == true;

        if (enabled)
        {
            var s = _vm.Simulation.PlcSettings;
            var tagCount = _vm.Simulation.CountAutoImportablePlcAddresses();
            PlcStatusDot.Fill = PlcOnBrush;
            PlcStatusText.Text =
                $"PLC 연결: 사용  ·  {s.Vendor}  {s.IpAddress}:{s.Port}  ·  IO 자동 import {tagCount}개";
        }
        else
        {
            PlcStatusDot.Fill = PlcOffBrush;
            PlcStatusText.Text = requiresPlc
                ? "PLC 연결: 사용 안 함"
                : "PLC 연결: 해당 없음 (시뮬레이션 / 가상 시운전 모드)";
        }
    }

    /// <summary>
    /// 라디오 카드 한 장에 바인딩되는 모드 VM.
    /// IsSelected 는 카드 클릭으로 토글되어 테두리 강조 트리거에 사용.
    /// </summary>
    private sealed partial class ModeItemVM : ObservableObject
    {
        public required RuntimeMode Mode   { get; init; }   // "Simulation" / "Control" / "Monitoring" / "VirtualPlant"
        public required string NameKr      { get; init; }   // "시뮬레이션"
        public required string Description { get; init; }   // 한 줄 한글 설명
        public required string LeftLabel   { get; init; }   // "PC 로직" 등
        public required string RightLabel  { get; init; }   // "가상 모델" 등
        public required Brush  LeftAccent  { get; init; }
        public required Brush  RightAccent { get; init; }
        public required Visibility ForwardVisibility { get; init; }  // 출력(→) 표시 여부 — Monitoring/Simulation 은 Collapsed
        public required bool IsInternalLoop { get; init; }  // true = Sim 전용 점선 박스 + 가로 배치 콘텐츠
        public required string SoloHeading { get; init; }   // Sim 박스 큰 글자 (다른 모드는 "")
        public required string ModeFolder  { get; init; }   // "sim" | "ctrl" | "mon" | "vp"
        public required string LeftSide    { get; init; }   // "ctrl" | "mode"
        public required string RightSide   { get; init; }   // "plant"

        [ObservableProperty] private ImageSource? _leftThumb;
        [ObservableProperty] private ImageSource? _rightThumb;
        [ObservableProperty] private bool _isSelected;
    }

    private static SolidColorBrush FreezeBrush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    private static readonly SolidColorBrush BlueBrush   = FreezeBrush("#38bdf8");
    private static readonly SolidColorBrush OrangeBrush = FreezeBrush("#ff7b54");
    private static readonly SolidColorBrush GreenBrush  = FreezeBrush("#34d399");
    private static readonly SolidColorBrush PurpleBrush = FreezeBrush("#a78bfa");

    // 하단 푸터 PLC 상태 점 — 사용(초록) / 미사용(회색)
    private static readonly SolidColorBrush PlcOnBrush  = FreezeBrush("#34d399");
    private static readonly SolidColorBrush PlcOffBrush = FreezeBrush("#6b7280");

    private static List<ModeItemVM> BuildItems(RuntimeMode currentMode)
    {
        var items = BuildModeItems();
        foreach (var item in items)
            item.IsSelected = item.Mode == currentMode;
        return items;
    }

    private static List<ModeItemVM> BuildModeItems() =>
    [
        new ModeItemVM
        {
            Mode = RuntimeMode.Simulation, NameKr = "시뮬레이션",
            Description = "장비 연결 없이 로직 테스트와 토큰 흐름을 미리 확인합니다 — 로직 제어와 상태 업데이트를 Promaker 가 스스로 처리합니다.",
            LeftLabel = "PC 로직",     RightLabel = "노드 흐름",
            LeftAccent = BlueBrush,    RightAccent = BlueBrush,
            ForwardVisibility = Visibility.Collapsed,
            IsInternalLoop = true,
            SoloHeading = "Promaker 단일 실행",
            ModeFolder = "sim", LeftSide = "ctrl", RightSide = "plant",
        },
        new ModeItemVM
        {
            Mode = RuntimeMode.Control, NameKr = "제어",
            Description = "제어기 역할을 수행합니다 — 로직에 따라 Output 으로 출력을 내보내고 Input 을 받아 상태를 업데이트합니다.",
            LeftLabel = "실제 제어",   RightLabel = "실제 설비",
            LeftAccent = OrangeBrush,  RightAccent = OrangeBrush,
            ForwardVisibility = Visibility.Visible,
            IsInternalLoop = false,
            SoloHeading = "",
            ModeFolder = "ctrl", LeftSide = "mode", RightSide = "plant",
        },
        new ModeItemVM
        {
            Mode = RuntimeMode.Monitoring, NameKr = "모니터링",
            Description = "상태 모니터링 역할을 수행합니다 — Input/Output 둘 다 받아 현재 노드들의 상태를 유추해 보여줍니다.",
            LeftLabel = "모니터링",    RightLabel = "실제 설비",
            LeftAccent = BlueBrush,    RightAccent = OrangeBrush,
            ForwardVisibility = Visibility.Collapsed,
            IsInternalLoop = false,
            SoloHeading = "",
            ModeFolder = "mon", LeftSide = "mode", RightSide = "plant",
        },
        new ModeItemVM
        {
            Mode = RuntimeMode.VirtualPlant, NameKr = "가상 시운전",
            Description = "가상 설비 역할을 수행합니다 — Output 신호를 받아 로직을 처리하고 상태를 업데이트해 Input 으로 되돌려줍니다.",
            LeftLabel = "실제 제어",    RightLabel = "가상 설비",
            LeftAccent = OrangeBrush,  RightAccent = PurpleBrush,
            ForwardVisibility = Visibility.Visible,
            IsInternalLoop = false,
            SoloHeading = "",
            ModeFolder = "vp", LeftSide = "ctrl", RightSide = "plant",
        },
    ];

    /// <summary>각 VM 의 좌·우 썸네일을 갱신. IsInternalLoop=true 인 카드는 RightThumb 미사용이라 skip.</summary>
    private void RefreshThumbnails()
    {
        foreach (var vm in _items)
        {
            vm.LeftThumb = LoadIcon(vm.ModeFolder, vm.LeftSide);
            if (!vm.IsInternalLoop)
                vm.RightThumb = LoadIcon(vm.ModeFolder, vm.RightSide);
        }
    }

    private static BitmapImage? LoadIcon(string mode, string side)
    {
        var uri = new Uri(
            $"pack://application:,,,/Promaker;component/Assets/Runtime/{VariantKey}/{mode}_{side}.png",
            UriKind.Absolute);
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = uri;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Error($"Runtime icon load failed: {uri}", ex);
            return null;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        // CanResize + Aero Snap 으로 최대화될 수 있는데, 최대화 상태에서 DragMove 호출 시 예외가 난다.
        if (WindowState == WindowState.Maximized) return;
        try { DragMove(); } catch { /* 드물게 발생하는 입력 레이스 무시 */ }
    }

    /// <summary>카드 전체 영역 클릭 → 해당 VM 선택 (나머지 선택 해제). 기존 RadioButton 을 대체.</summary>
    private void ModeCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not ModeItemVM clicked) return;
        SelectMode(clicked);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var selected = _items.FirstOrDefault(v => v.IsSelected);
        if (selected != null)
            _vm.Simulation.SelectedRuntimeMode = selected.Mode;
        // HubAddress 는 TextBox 가 TwoWay 바인딩이라 자동 반영됨.
        // PLC 토글은 Control/Monitoring 모드에서만 의미가 있음 — 다른 모드 선택 시엔 자동 해제.
        var requiresPlc = selected is not null
            && (selected.Mode == RuntimeMode.Control || selected.Mode == RuntimeMode.Monitoring);
        _vm.Simulation.IsRealPlcConnected = requiresPlc && RealPlcCheckBox.IsChecked == true;

        DialogResult = true;
        Close();
    }
}
