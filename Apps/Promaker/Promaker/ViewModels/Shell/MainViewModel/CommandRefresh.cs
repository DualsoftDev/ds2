using Ds2.Core;
using Ds2.Editor;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    /// <summary>RelayCommand CanExecute 일괄 재평가 collaborator. ctor 에서 wire.</summary>
    private EditorCommandRefresher _editorCommandRefresher = null!;

    internal void RefreshEditorCommandStates()
    {
        NormalizeConnectArrowTypeForActiveTab();
        _editorCommandRefresher.Refresh();
    }

    /// <summary>F# RefreshScope 기반 visual 갱신 단일 진입점.
    /// HandleEvent 가 EditorEvent 별 특수 사이드이펙트를 처리한 뒤 visual refresh 부분만 본 메서드로 위임.
    /// Tree-only refresh path 가 별도로 없으므로 Tree 비트가 있으면 RebuildAll fallback.</summary>
    private void ApplyRefreshScope(RefreshScope scope)
    {
        if (scope == RefreshScope.None)
            return;

        if (scope.Contains(RefreshScope.Tree))
        {
            // Tree 재구축은 캔버스 재구축과 분리돼 있다. Canvas 비트가 없는 trigger(속성 변경 등)는
            // 캔버스 노드 집합이 그대로이므로 pane 을 다시 만들지 않는다 — 캔버스는 가상화가 없어
            // 노드당 89 엘리먼트를 전부 새로 인플레이트하는 쪽이 이 경로에서 제일 비쌌다.
            if (scope.Contains(RefreshScope.Canvas))
                RequestRebuildAll();
            else
                RequestRebuildTrees();
            return;
        }

        if (scope.Contains(RefreshScope.Canvas))
            CanvasManager.ApplyConnectionsChangedToAllPanes();

        if (scope.Contains(RefreshScope.PropertyPanel))
            PropertyPanel.Refresh();

        if (scope.Contains(RefreshScope.CommandAvailability))
            _editorCommandRefresher.RefreshFor(scope);
    }

    private void NormalizeConnectArrowTypeForActiveTab()
    {
        if (Canvas.ActiveTab is not { } tab) return;

        if (!EntityKindRules.isWorkArrowModeForTab(tab.Kind)
            && SelectedConnectArrowType is ArrowType.Reset or ArrowType.StartReset or ArrowType.ResetReset)
        {
            SelectedConnectArrowType = ArrowType.Start;
        }
    }
}
