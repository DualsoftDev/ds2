using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;

namespace Promaker.ViewModels;

public partial class PropertyPanelState
{
    public void ApplyEntityRename(Guid entityId, string newName)
    {
        if (SelectedNode is { Id: var selectedId } && selectedId == entityId)
        {
            // entity kind 별 prefix/editable/suffix 분해는 F# NameEditorParts 위임.
            var parts = SelectedNode.EntityType switch
            {
                EntityKind.Work => NameEditorParts.ForWork(newName),
                EntityKind.Call => NameEditorParts.ForCall(newName),
                _ => NameEditorParts.ForFallback(newName),
            };
            NamePrefix = parts.Prefix;
            NameEditorText = parts.Editable;
            NameSuffix = parts.Suffix;
            IsNameDirty = false;
            IsNameEditHighlighted = false;
        }
    }

    public void BeginNameEditGuidance() => IsNameEditHighlighted = true;

    public void ClearNameEditGuidance() => IsNameEditHighlighted = false;

    public void CancelNameEdit()
    {
        if (SelectedNode is null)
        {
            IsNameEditHighlighted = false;
            return;
        }

        var fullName = SelectedNode.Name ?? string.Empty;
        // Work 는 Store 에서 full name 조회 후 분해, Call/Other 는 raw name.
        if (SelectedNode.EntityType == EntityKind.Work)
        {
            var workName = Queries.tryGetWorkFullName(SelectedNode.Id, Store);
            if (workName != null) fullName = workName.Value;
        }
        var parts = SelectedNode.EntityType switch
        {
            EntityKind.Work => NameEditorParts.ForWork(fullName),
            EntityKind.Call => NameEditorParts.ForCall(fullName),
            _ => NameEditorParts.ForFallback(fullName),
        };
        NamePrefix = parts.Prefix;
        NameEditorText = parts.Editable;
        NameSuffix = parts.Suffix;

        IsNameDirty = false;
        IsNameEditHighlighted = false;
    }

    private bool CanApplyName() =>
        IsSingleSelection && SelectedNode is not null && !string.IsNullOrWhiteSpace(NameEditorText);

    [RelayCommand(CanExecute = nameof(CanApplyName))]
    private void ApplyName()
    {
        if (SelectedNode is null) return;

        var localName = NameEditorText.Trim();
        if (string.IsNullOrEmpty(localName))
        {
            _host.SetStatusText("Name cannot be empty.");
            return;
        }

        // prefix/suffix 가 있으면 전체 이름으로 결합해 전달 (RenameEntity 가 다시 분리함)
        var newName = NamePrefix + localName + NameSuffix;
        _host.RenameSelected(newName);
        IsNameEditHighlighted = false;
    }

    [RelayCommand]
    private void ApplyWorkPeriod()
    {
        var selectedWorkIds = GetSelectedCanonicalWorkIds();
        if (selectedWorkIds.Count == 0) return;

        // 시뮬레이션 중 Going Work가 포함되어 있으면 경고 후 거부
        // (사용자가 "시뮬레이션 종료"를 선택하면 시뮬을 정지하고 변경을 진행)
        if (_host.IsSimulating)
        {
            var goingIds = selectedWorkIds
                .Where(id => _host.GetSimWorkState(id) == Ds2.Core.Status4.Going)
                .ToList();
            if (goingIds.Count > 0)
            {
                var msg = "Going 상태인 Work의 Duration은 변경할 수 없습니다.\n\n계속하려면 시뮬레이션을 종료해야 합니다.";
                if (!_host.TryStopSimulationViaWarning(msg))
                    return;
                // 시뮬 종료 성공 → 정상 경로로 fall-through
            }
        }

        if (IsSingleWorkSelected && _deviceDurationMs is { } devMs)
        {
            var userMs = WorkPeriodMs ?? 0;
            var ruleText = userMs > devMs
                ? $"설정값({userMs}ms)이 예상 시간({devMs}ms)보다 크므로 설정값이 적용됩니다."
                : $"설정값({userMs}ms)이 예상 시간({devMs}ms)보다 작으므로 예상 시간이 우선됩니다.";
            var result = Dialogs.DialogHelpers.ShowThemedMessageBox(
                $"이 Work의 예상 소요 시간이 {devMs}ms로 산출되어 있습니다.\n" +
                $"{ruleText}\n\n계속하시겠습니까?",
                "Duration 안내",
                System.Windows.MessageBoxButton.YesNo, "ℹ");
            if (result != System.Windows.MessageBoxResult.Yes) return;
        }

        var changeValue = WorkPeriodMs;
        var changes = selectedWorkIds.Select(workId => new ValueTuple<Guid, int?>(workId, changeValue)).ToList();

        if (!_host.TryAction(() => Store.UpdateWorkPeriodsBatch(changes)))
            return;

        // 시뮬레이션 중이면 엔진에 Duration 변경 반영
        if (_host.IsSimulating)
            _host.ReloadSimDurations();

        _originalWorkPeriodMs = WorkPeriodMs;
        IsWorkPeriodDirty = false;
        _host.SetStatusText(selectedWorkIds.Count > 1
            ? $"Work period updated for {selectedWorkIds.Count} items."
            : "Work period updated.");
        Refresh();
    }

    private int? LoadOptionalMsFromStore(Guid entityId, Func<Guid, int?> getter)
    {
        if (_host.TryFunc(() => getter(entityId), out int? value, null))
            return value;
        return null;
    }
}
