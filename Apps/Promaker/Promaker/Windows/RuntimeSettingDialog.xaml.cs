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

    public RuntimeSettingDialog(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm.Simulation;   // HubAddress · NeedsHubConnection 양방향 바인딩

        _items = BuildItems(vm.Simulation.SelectedRuntimeMode);
        ModeList.ItemsSource = _items;
        RefreshThumbnails();
        SyncHubAddressVisibility();
    }

    /// <summary>선택된 카드의 모드에 따라 HubAddress 입력 영역을 즉시 show/hide.
    /// Apply 전에도 카드 클릭만으로 반응하도록 ViewModel.NeedsHubConnection 대신 미리보기 IsSelected 기반 제어.</summary>
    private void SyncHubAddressVisibility()
    {
        var selected = _items.FirstOrDefault(v => v.IsSelected);
        var needsHub = selected != null && selected.Mode != RuntimeMode.Simulation;
        HubAddressArea.Visibility = needsHub ? Visibility.Visible : Visibility.Collapsed;
        SelectedModeLabel.Text = selected?.Mode.ToString() ?? "";
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
            Description = "상태 모니터링 역할을 수행합니다 — Input 만 받아 현재 노드들의 상태를 유추해 보여줍니다.",
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
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    /// <summary>카드 전체 영역 클릭 → 해당 VM 선택 (나머지 선택 해제). 기존 RadioButton 을 대체.</summary>
    private void ModeCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not ModeItemVM clicked) return;

        foreach (var vm in _items)
            vm.IsSelected = ReferenceEquals(vm, clicked);
        SyncHubAddressVisibility();
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
        DialogResult = true;
        Close();
    }
}
