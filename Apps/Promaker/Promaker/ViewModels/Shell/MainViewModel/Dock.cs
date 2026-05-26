using AvalonDock.Layout;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Promaker.ViewModels;

// B-1 (`Apps/Promaker/Docs/todo-dock-layout.md` §3.1 Q2) — 보기 메뉴 가 anchor 의 IsVisible 을 TwoWay binding 하기 위해
// LayoutAnchorable 들을 VM 노출. LlmChat 은 IsLlmChatVisible SSOT 별도라 anchor 자체는 노출 안 함 (보기 메뉴 CheckBox 는
// ToggleLlmChatCommand + IsLlmChatVisible OneWay 표시 패턴).
// XAML 의 MainToolbarEtcContent 가 별도 UserControl 이라 ElementName 으로 anchor 직접 접근 불가 → VM mirror 가 가장 짧음.
// LogAnchor — `SimulationPanel` 의 Log tab 이 별도 독립 dock pane 으로 분리됨 (`Controls/Logging/AppLogView`).
//             HasProject 와 무관하게 항상 활성 가능 (앱 전역 log).
public partial class MainViewModel
{
    [ObservableProperty] private LayoutAnchorable? _explorerAnchor;
    [ObservableProperty] private LayoutAnchorable? _propertyAnchor;
    [ObservableProperty] private LayoutAnchorable? _historyAnchor;
    [ObservableProperty] private LayoutAnchorable? _simulationAnchor;
    [ObservableProperty] private LayoutAnchorable? _logAnchor;
}
