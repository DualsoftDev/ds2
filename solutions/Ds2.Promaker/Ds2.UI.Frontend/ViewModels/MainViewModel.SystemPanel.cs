using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.UI.Core;
using Ds2.UI.Frontend.Dialogs;

namespace Ds2.UI.Frontend.ViewModels;

public partial class MainViewModel
{
    private bool TryGetWorksForSystem(Guid systemId, out List<WorkDropdownItem> works) =>
        TryEditorFunc(
            "GetWorksForSystem",
            () => _editor.GetWorksForSystem(systemId).ToList(),
            out works,
            fallback: []);

    private bool TryUpdateApiDefProperties(Guid apiDefId, ApiDefEditDialog dialog) =>
        TryEditorAction(
            "UpdateApiDefProperties",
            () => _editor.UpdateApiDefProperties(
                apiDefId,
                dialog.IsPush,
                dialog.TxWorkId,
                dialog.RxWorkId,
                dialog.Duration,
                dialog.Description));

    private bool TryRenameApiDefIfNeeded(Guid apiDefId, string currentName, string nextName) =>
        nextName == currentName ||
        TryEditorAction(
            "RenameEntity",
            () => _editor.RenameEntity(apiDefId, EntityTypes.ApiDef, nextName));

    [RelayCommand]
    private void AddSystemApiDef()
    {
        if (!TryGetSelectedNode(EntityTypes.System, out var systemNode)) return;

        if (!TryGetWorksForSystem(systemNode.Id, out var works))
            return;

        var dialog = new ApiDefEditDialog(works);
        if (!ShowOwnedDialog(dialog)) return;

        if (!TryEditorFunc(
                "AddApiDefAndGetId",
                () => _editor.AddApiDefAndGetId(dialog.ApiDefName, systemNode.Id),
                out var newApiDefId,
                fallback: Guid.Empty))
            return;

        if (newApiDefId == Guid.Empty)
            return;

        if (!TryUpdateApiDefProperties(newApiDefId, dialog))
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

        if (!TryRenameApiDefIfNeeded(item.Id, item.Name, dialog.ApiDefName))
            return;

        if (!TryUpdateApiDefProperties(item.Id, dialog))
            return;

        RefreshSystemPanel(systemNode.Id);
        StatusText = $"ApiDef '{dialog.ApiDefName}' updated.";
    }

    [RelayCommand]
    private void DeleteSystemApiDef(ApiDefPanelItem? item)
    {
        if (item is null || !TryGetSelectedNode(EntityTypes.System, out var systemNode)) return;

        if (!TryEditorAction(
                "RemoveEntities",
                () => _editor.RemoveEntities(new[] { Tuple.Create(EntityTypes.ApiDef, item.Id) })))
            return;

        RefreshSystemPanel(systemNode.Id);
        StatusText = $"ApiDef '{item.Name}' deleted.";
    }

    private void RefreshSystemPanel(Guid systemId)
    {
        if (!TryEditorRef(
                "GetApiDefsForSystem",
                () => _editor.GetApiDefsForSystem(systemId),
                out var items))
            return;

        SystemApiDefs.Clear();
        foreach (var item in items)
            SystemApiDefs.Add(item);
    }

    public void EditApiDefNode(Guid apiDefId)
    {
        if (!TryEditorRef(
                "GetApiDefParentSystemId",
                () => _editor.GetApiDefParentSystemId(apiDefId),
                out var systemIdOpt))
            return;

        var systemId = systemIdOpt.Value;

        if (!TryEditorFunc(
                "GetApiDefsForSystem",
                () => _editor.GetApiDefsForSystem(systemId).FirstOrDefault(x => x.Id == apiDefId),
                out ApiDefPanelItem? existing,
                fallback: null))
            return;

        if (existing is null)
            return;

        if (!TryGetWorksForSystem(systemId, out var works))
            return;

        var dialog = new ApiDefEditDialog(works, existing);
        if (!ShowOwnedDialog(dialog)) return;

        if (!TryRenameApiDefIfNeeded(apiDefId, existing.Name, dialog.ApiDefName))
            return;

        if (!TryUpdateApiDefProperties(apiDefId, dialog))
            return;

        if (IsSystemSelected && SelectedNode?.Id == systemId)
            RefreshSystemPanel(systemId);

        StatusText = $"ApiDef '{dialog.ApiDefName}' updated.";
    }
}
