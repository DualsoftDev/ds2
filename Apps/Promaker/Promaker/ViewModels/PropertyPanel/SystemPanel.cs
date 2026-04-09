using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core.Store;
using Ds2.Editor;
using Promaker.Dialogs;

namespace Promaker.ViewModels;

public partial class PropertyPanelState
{
    private bool TryShowApiDefDialog(Guid systemId, ApiDefPanelItem? existing, out ApiDefEditDialog dialog)
    {
        dialog = null!;
        if (!_host.TryRef(() => Store.GetWorksForSystem(systemId), out var works))
            return false;
        dialog = existing is not null ? new ApiDefEditDialog(works, existing) : new ApiDefEditDialog(works);
        return ShowOwnedDialog(dialog);
    }

    private bool TryUpdateApiDef(Guid apiDefId, ApiDefEditDialog dialog) =>
        _host.TryAction(
            () => Store.UpdateApiDef(apiDefId, dialog.ApiDefName));

    [RelayCommand]
    private void AddSystemApiDef()
    {
        if (!GuardSimulationSemanticEdit("ApiDef 추가"))
            return;

        if (!TryGetSelectedNode(EntityKind.System, out var systemNode)) return;
        if (!TryShowApiDefDialog(systemNode.Id, null, out var dialog)) return;

        if (!_host.TryAction(
                () => Store.AddApiDefWithProperties(
                    dialog.ApiDefName, systemNode.Id)))
            return;

        RefreshSystemPanel(systemNode.Id);
        _host.SetStatusText($"ApiDef '{dialog.ApiDefName}' added.");
    }

    [RelayCommand]
    private void EditSystemApiDef(ApiDefPanelItem? item)
    {
        if (!GuardSimulationSemanticEdit("ApiDef 편집"))
            return;

        if (item is null || !TryGetSelectedNode(EntityKind.System, out var systemNode)) return;
        if (!TryShowApiDefDialog(systemNode.Id, item, out var dialog)) return;
        if (!TryUpdateApiDef(item.Id, dialog)) return;

        RefreshSystemPanel(systemNode.Id);
        _host.SetStatusText($"ApiDef '{dialog.ApiDefName}' updated.");
    }

    [RelayCommand]
    private void DeleteSystemApiDef(ApiDefPanelItem? item)
    {
        if (!GuardSimulationSemanticEdit("ApiDef 삭제"))
            return;

        if (item is null || !TryGetSelectedNode(EntityKind.System, out var systemNode)) return;

        if (!_host.TryAction(
                () => Store.RemoveEntities(new[] { Tuple.Create(EntityKind.ApiDef, item.Id) })))
            return;

        RefreshSystemPanel(systemNode.Id);
        _host.SetStatusText($"ApiDef '{item.Name}' deleted.");
    }

    [RelayCommand]
    private void ApplySystemType()
    {
        if (RequireSelectedAs(EntityKind.System) is not { } selectedSystem) return;

        if (!GuardSimulationSemanticEdit("System type 변경"))
            return;

        if (!_host.TryAction(() => Store.UpdateSystemType(selectedSystem.Id, SystemType)))
            return;

        _originalSystemType = SystemType;
        IsSystemTypeDirty = false;
        _host.SetStatusText("System type updated.");
    }

    private void RefreshSystemPanel(Guid systemId)
    {
        if (!_host.TryRef(
                () => Store.GetApiDefsForSystem(systemId),
                out var items))
            return;

        ReplaceAll(SystemApiDefs, items);
    }

    public void EditApiDefNode(Guid apiDefId)
    {
        if (!_host.TryRef(
                () => Store.TryGetApiDefForEditOrNull(apiDefId),
                out var info))
            return;

        var systemId = info.SystemId;
        var existing = info.Item;
        if (!TryShowApiDefDialog(systemId, existing, out var dialog)) return;
        if (!TryUpdateApiDef(apiDefId, dialog)) return;

        if (IsSystemSelected && SelectedNode?.Id == systemId)
            RefreshSystemPanel(systemId);

        _host.SetStatusText($"ApiDef '{dialog.ApiDefName}' updated.");
    }
}
