using CommunityToolkit.Mvvm.ComponentModel;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    /// <summary>
    /// PR-D5 — 보기 메뉴 (`MainToolbarEtcContent.xaml`) 의 4 체크박스 binding source.
    /// MainWindow.xaml.cs 가 PropertyChanged 를 구독해 <see cref="Dock.IDockManager.SetAnchorVisible"/>
    /// 호출. 역방향 (X 버튼 → AnchorVisibilityChanged event → 본 property 단방향 set) 은
    /// MainWindow 의 `_suppressAnchorSync` 가드로 loop 차단.
    ///
    /// LlmChat 은 별도 SSOT (<see cref="IsLlmChatVisible"/>, baseline 박제 — todo §3 PR-D5 § "LlmChat.cs baseline 박제") 유지.
    /// Log 는 anchor 가 별도로 없음 — PR-D4 에서 SimulationPanel TabControl 마지막 tab 으로 통합. 보기 메뉴 제거.
    /// </summary>
    [ObservableProperty] private bool _isExplorerVisible = true;
    [ObservableProperty] private bool _isSimulationVisible = true;
    [ObservableProperty] private bool _isPropertiesVisible = true;
    [ObservableProperty] private bool _isHistoryVisible = true;
}
