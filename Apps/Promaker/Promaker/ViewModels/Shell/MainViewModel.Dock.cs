using AvalonDock.Layout;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Promaker.ViewModels;

// B-1 (`Apps/Promaker/Docs/todo-dock-layout.md` §3.1 Q2) — 보기 메뉴 가 anchor 의 IsVisible 을 TwoWay binding 하기 위해
// LayoutAnchorable 4종 (Explorer/Property/History/Simulation) 을 VM 노출. LlmChat 은 IsLlmChatVisible SSOT 별도라 제외.
// XAML 의 MainToolbarEtcContent 가 별도 UserControl 이라 ElementName 으로 anchor 직접 접근 불가 → VM mirror 가 가장 짧음.
public partial class MainViewModel
{
    [ObservableProperty] private LayoutAnchorable? _explorerAnchor;
    [ObservableProperty] private LayoutAnchorable? _propertyAnchor;
    [ObservableProperty] private LayoutAnchorable? _historyAnchor;
    [ObservableProperty] private LayoutAnchorable? _simulationAnchor;
}
