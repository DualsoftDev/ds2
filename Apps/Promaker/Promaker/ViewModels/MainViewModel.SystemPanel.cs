using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.UI.Core;
using Promaker.Dialogs;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    private bool TryGetWorksForSystem(Guid systemId, out List<WorkDropdownItem> works) =>
        TryEditorFunc(
            () => _store.GetWorksForSystem(systemId).ToList(),
            out works,
            fallback: []);

    [RelayCommand]
    private void AddSystemApiDef()
    {
        if (!TryGetSelectedNode(EntityTypes.System, out var systemNode)) return;

        if (!TryGetWorksForSystem(systemNode.Id, out var works))
            return;

        var dialog = new ApiDefEditDialog(works);
        if (!ShowOwnedDialog(dialog)) return;

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

        if (!TryGetWorksForSystem(systemNode.Id, out var works))
            return;

        var dialog = new ApiDefEditDialog(works, item);
        if (!ShowOwnedDialog(dialog)) return;

        if (!TryEditorAction(
                () => _store.UpdateApiDef(
                    item.Id, dialog.ApiDefName, dialog.IsPush,
                    dialog.TxWorkId, dialog.RxWorkId, dialog.Period, dialog.Description)))
            return;

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

        SystemApiDefs.Clear();
        foreach (var item in items)
            SystemApiDefs.Add(item);
    }

    public void EditApiDefNode(Guid apiDefId)
    {
        if (!TryEditorFunc(
                () => _store.TryGetApiDefForEdit(apiDefId),
                out var editInfo,
                fallback: null))
            return;

        if (editInfo is not { } info)
            return;

        var (systemId, existing) = info.Value;

        if (!TryGetWorksForSystem(systemId, out var works))
            return;

        var dialog = new ApiDefEditDialog(works, existing);
        if (!ShowOwnedDialog(dialog)) return;

        if (!TryEditorAction(
                () => _store.UpdateApiDef(
                    apiDefId, dialog.ApiDefName, dialog.IsPush,
                    dialog.TxWorkId, dialog.RxWorkId, dialog.Period, dialog.Description)))
            return;

        if (IsSystemSelected && SelectedNode?.Id == systemId)
            RefreshSystemPanel(systemId);

        StatusText = $"ApiDef '{dialog.ApiDefName}' updated.";
    }
}