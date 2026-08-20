using System;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Promaker.Dialogs;
using Promaker.Services;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    /// <summary>
    /// IO·태그 확인 다이얼로그 — 다이얼로그가 IoQueryService 로 자체 생성/새로고침 한다.
    /// System(PLC) 필터의 초기값은 현재 컨텍스트(선택 노드 → 활성 캔버스 탭)의 System.
    /// 진단 카드의 "FBTagMap 편집" 버튼은 시뮬레이션 가드 통과 후 TAG Wizard 를
    /// 해당 SystemType 으로 바로 열어준다 (편집 후 자동 새로고침).
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasProject))]
    private void OpenTagInspector()
    {
        if (!GuardSimulationSemanticEdit("IO·태그 확인"))
            return;
        _dialogService.ShowDialog(new TagInspectorDialog(_store, OpenFBTagMapEditFor, ResolveContextSystemId()));
    }

    /// <summary>현재 컨텍스트의 System(PLC) — 선택 노드 우선, 없으면 활성 캔버스 탭에서 도출.</summary>
    private Guid? ResolveContextSystemId()
    {
        var (selKind, selId, tabKind, tabRoot) = SnapshotContext();
        return TrySystemIdFor(selKind, selId)
            ?? (tabKind is { } tk ? TrySystemIdFor(EditorNavigation.EntityKindForTabKind(tk), tabRoot) : null);
    }

    private Guid? TrySystemIdFor(EntityKind? kind, Guid? entityId)
    {
        if (kind is not { } k || entityId is not { } id) return null;
        var opt = StoreHierarchyQueries.tryFindSystemIdForEntity(_store, k, id);
        return Microsoft.FSharp.Core.FSharpOption<Guid>.get_IsSome(opt) ? opt.Value : null;
    }

    /// <summary>IO·태그 확인 → "FBTagMap 편집" 버튼 콜백.</summary>
    private void OpenFBTagMapEditFor(string? systemType)
    {
        if (!GuardSimulationSemanticEdit("FBTagMap 편집"))
            return;

        var wizard = new TagWizardDialog(_store);
        wizard.OpenAtFBTagMapForSystemType(systemType);
        _dialogService.ShowDialog(wizard);
    }

    /// <summary>
    /// I/O 생성 — 모드 선택 후 기본/고급 Wizard 로 라우팅.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasProject))]
    private void OpenTagWizard()
    {
        if (!GuardSimulationSemanticEdit("IO 생성 (TAG Wizard)"))
            return;

        var picker = new TagWizardModeDialog();
        if (_dialogService.ShowDialog(picker) != true)
            return;

        switch (picker.SelectedMode)
        {
            case TagWizardModeDialog.WizardMode.Advanced:
                _dialogService.ShowDialog(new TagWizardDialog(_store));
                break;
            case TagWizardModeDialog.WizardMode.Basic:
            default:
                _dialogService.ShowDialog(new TagWizardBasicDialog(_store, ResolveContextSystemId()));
                break;
        }
    }

    // PR-A5 — OpenSymbolWizard / SymbolWizardDialog 제거. PLC 심볼 import 는 별도 툴로 분리 예정.
}
