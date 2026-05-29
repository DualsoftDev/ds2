using CommunityToolkit.Mvvm.ComponentModel;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    /// <summary>
    /// 상단 Ribbon (`MainToolbar.xaml`) 의 6 카테고리 (파일/프로젝트/편집/연결/시뮬레이션/기타) 보기/숨김 상태.
    ///
    /// Binding 출처:
    ///   - 각 Section 의 <c>Visibility</c> (Section + 다음 Separator 동시 hide — Section 만 hide 시
    ///     trailing separator 가 시각적으로 남는 문제 차단).
    ///   - <c>MainToolbar.xaml</c> 최상단 <c>&lt;Border&gt;</c> 의 <c>ContextMenu</c> 의 6 <c>MenuItem</c>
    ///     (<c>IsCheckable=True</c>, <c>IsChecked</c> TwoWay binding) — 우클릭 toggle UI.
    ///
    /// 기존 Dock anchor 의 시뮬레이션 panel 가시성 (<see cref="IsSimulationVisible"/>, DockAnchors.cs) 과는
    /// 의미가 다름: 본 property 는 ribbon 상단 "시뮬레이션" 섹션 (PLAY/PAUSE/Work/속도) 의 가시성만 제어.
    ///
    /// 영속화: 본 PR 범위 외 — 현재 in-memory 상태 only (앱 재시작 시 default true 복귀).
    ///         후속 PR 에서 settings xml / LayoutXml 동등 저장 영역으로 박제 예정.
    ///
    /// HasProject 무관 — 모든 카테고리 가시성은 사용자 선호 only (프로젝트 로드 상태와 독립).
    /// </summary>
    [ObservableProperty] private bool _isToolbarFileVisible = true;
    [ObservableProperty] private bool _isToolbarProjectVisible = true;
    [ObservableProperty] private bool _isToolbarEditVisible = true;
    [ObservableProperty] private bool _isToolbarConnectVisible = true;
    [ObservableProperty] private bool _isToolbarSimulationVisible = true;
    [ObservableProperty] private bool _isToolbarEtcVisible = true;
}
