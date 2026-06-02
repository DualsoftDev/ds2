using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    /// Explorer 하단 "비활성화" 섹션에 표시되는 비활성 Flow 목록 (Control System 한정, 평면).
    /// RebuildAll 에서 EditorTreeProjection.DisabledFlows 로 채운다.
    public ObservableCollection<EntityNode> DisabledFlows { get; } = [];

    /// 트리에서 선택된 Flow(들)을 비활성화한다 (다중 선택 = 1 undo step). 우클릭 메뉴용.
    [RelayCommand(CanExecute = nameof(CanDisableSelectedFlows))]
    private void DisableSelectedFlows() => DisableFlows(SelectedFlowIds());

    private bool CanDisableSelectedFlows() => HasProject && SelectedFlowIds().Count > 0;

    /// 비활성화 섹션의 Flow 를 복원한다. 우클릭 메뉴용 (단일 노드).
    [RelayCommand]
    private void RestoreFlow(EntityNode? node)
    {
        if (node is { EntityType: EntityKind.Flow })
            RestoreFlows([node.Id]);
    }

    /// 비활성화 — 드래그&드롭/명령 공용. 편집 의미(IsDisabled 토글 + Undo)는 F# SetFlowsDisabled 가 담당.
    /// 비활성 Flow 하위 Work 탭은 tabTitle→ValidateAndRefresh 경로로 자동 정리된다.
    public void DisableFlows(IReadOnlyList<Guid> flowIds)
    {
        if (flowIds.Count == 0) return;
        if (TryEditorAction(() => { _store.SetFlowsDisabled(flowIds, true); }))
            StatusText = $"{flowIds.Count}개 Flow 비활성화";
    }

    /// 복원 — 드래그&드롭/명령 공용.
    public void RestoreFlows(IReadOnlyList<Guid> flowIds)
    {
        if (flowIds.Count == 0) return;
        if (TryEditorAction(() => { _store.SetFlowsDisabled(flowIds, false); }))
            StatusText = $"{flowIds.Count}개 Flow 복원";
    }

    private IReadOnlyList<Guid> SelectedFlowIds() =>
        Selection.OrderedNodeSelection
            .Where(k => k.EntityKind == EntityKind.Flow)
            .Select(k => k.Id)
            .ToList();
}
