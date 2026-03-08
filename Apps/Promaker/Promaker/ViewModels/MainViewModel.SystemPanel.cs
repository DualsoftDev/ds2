using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.UI.Core;
using Promaker.Dialogs;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    private bool TryShowApiDefDialog(Guid systemId, ApiDefPanelItem? existing, out ApiDefEditDialog dialog)
    {
        dialog = null!;
        if (!TryEditorFunc(() => _store.GetWorksForSystem(systemId).ToList(), out List<WorkDropdownItem> works, fallback: []))
            return false;
        dialog = existing is not null ? new ApiDefEditDialog(works, existing) : new ApiDefEditDialog(works);
        return ShowOwnedDialog(dialog);
    }

    private bool TryUpdateApiDef(Guid apiDefId, ApiDefEditDialog dialog) =>
        TryEditorAction(
            () => _store.UpdateApiDef(
                apiDefId, dialog.ApiDefName, dialog.IsPush,
                dialog.TxWorkId, dialog.RxWorkId, dialog.Period, dialog.Description));

    [RelayCommand]
    private void AddSystemApiDef()
    {
        if (!TryGetSelectedNode(EntityTypes.System, out var systemNode)) return;
        if (!TryShowApiDefDialog(systemNode.Id, null, out var dialog)) return;

        if (!TryEditorAction(
                () => _store.AddApiDefWithProperties(
                    dialog.ApiDefName, systemNode.Id, dialog.IsPush,
                    dialog.TxWorkId, dialog.RxWorkId, dialog.Period, dialog.Description)))
            return;

        RefreshSystemPanel(systemNode.Id);
        StatusText = $"ApiDef '{dialog.ApiDefName}' added.";
    }

    [RelayCommand]
    private void EditSystemApiDef(ApiDefPanelItem? item)
    {
        if (item is null || !TryGetSelectedNode(EntityTypes.System, out var systemNode)) return;
        if (!TryShowApiDefDialog(systemNode.Id, item, out var dialog)) return;
        if (!TryUpdateApiDef(item.Id, dialog)) return;

        RefreshSystemPanel(systemNode.Id);
        StatusText = $"ApiDef '{dialog.ApiDefName}' updated.";
    }

    [RelayCommand]
    private void DeleteSystemApiDef(ApiDefPanelItem? item)
    {
        if (item is null || !TryGetSelectedNode(EntityTypes.System, out var systemNode)) return;

        if (!TryEditorAction(
                () => _store.RemoveEntities(new[] { Tuple.Create(EntityTypes.ApiDef, item.Id) })))
            return;

        RefreshSystemPanel(systemNode.Id);
        StatusText = $"ApiDef '{item.Name}' deleted.";
    }

    private void RefreshSystemPanel(Guid systemId)
    {
        if (!TryEditorRef(
                () => _store.GetApiDefsForSystem(systemId),
                out var items))
            return;

        ReplaceAll(SystemApiDefs, items);
    }

    public void EditApiDefNode(Guid apiDefId)
    {
        if (!TryEditorFunc(() => _store.TryGetApiDefForEdit(apiDefId), out var editInfo, fallback: null))
            return;

        if (editInfo is not { } info)
            return;

        var (systemId, existing) = info.Value;
        if (!TryShowApiDefDialog(systemId, existing, out var dialog)) return;
        if (!TryUpdateApiDef(apiDefId, dialog)) return;

        if (IsSystemSelected && SelectedNode?.Id == systemId)
            RefreshSystemPanel(systemId);

        StatusText = $"ApiDef '{dialog.ApiDefName}' updated.";
    }
}