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
            // Tree 전용 갱신 path 가 없으므로 RebuildAll 로 통합 처리 — Tree | Canvas | PropertyPanel 모두 커버.
            RequestRebuildAll();
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
