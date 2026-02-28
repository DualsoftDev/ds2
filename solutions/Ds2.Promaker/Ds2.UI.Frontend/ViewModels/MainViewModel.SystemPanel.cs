using System;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.UI.Core;
using Ds2.UI.Frontend.Dialogs;

namespace Ds2.UI.Frontend.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private void AddSystemApiDef()
    {
        if (RequireSelectedAs(EntityTypes.System) is not { } systemNode) return;

        var works = _editor.GetWorksForSystem(systemNode.Id).ToList();
        var dialog = new ApiDefEditDialog(works);
        if (!ShowOwnedDialog(dialog)) return;

        var newApiDefId = _editor.AddApiDefAndGetId(dialog.ApiDefName, systemNode.Id);
        _editor.UpdateApiDefProperties(newApiDefId, dialog.IsPush, dialog.TxWorkId, dialog.RxWorkId, dialog.Duration, dialog.Description);

        RefreshSystemPanel(systemNode.Id);
        StatusText = $"ApiDef '{dialog.ApiDefName}' added.";
    }

    [RelayCommand]
    private void EditSystemApiDef(ApiDefPanelItem? item)
    {
        if (item is null || RequireSelectedAs(EntityTypes.System) is not { } systemNode) return;

        var works = _editor.GetWorksForSystem(systemNode.Id).ToList();
        var dialog = new ApiDefEditDialog(works, item);
        if (!ShowOwnedDialog(dialog)) return;

        if (dialog.ApiDefName != item.Name)
            _editor.RenameEntity(item.Id, EntityTypes.ApiDef, dialog.ApiDefName);

        _editor.UpdateApiDefProperties(item.Id, dialog.IsPush, dialog.TxWorkId, dialog.RxWorkId, dialog.Duration, dialog.Description);

        RefreshSystemPanel(systemNode.Id);
        StatusText = $"ApiDef '{dialog.ApiDefName}' updated.";
    }

    [RelayCommand]
    private void DeleteSystemApiDef(ApiDefPanelItem? item)
    {
        if (item is null || RequireSelectedAs(EntityTypes.System) is not { } systemNode) return;

        _editor.RemoveEntities(new[] { Tuple.Create(EntityTypes.ApiDef, item.Id) });
        RefreshSystemPanel(systemNode.Id);
        StatusText = $"ApiDef '{item.Name}' deleted.";
    }

    private void RefreshSystemPanel(Guid systemId)
    {
        SystemApiDefs.Clear();
        foreach (var item in _editor.GetApiDefsForSystem(systemId))
            SystemApiDefs.Add(item);
    }

    public void EditApiDefNode(Guid apiDefId)
    {
        var systemIdOpt = _editor.GetApiDefParentSystemId(apiDefId);
        if (systemIdOpt is null) return;
        var systemId = systemIdOpt.Value;

        var existing = _editor.GetApiDefsForSystem(systemId).FirstOrDefault(x => x.Id == apiDefId);
        if (existing is null) return;

        var works = _editor.GetWorksForSystem(systemId).ToList();
        var dialog = new ApiDefEditDialog(works, existing);
        if (!ShowOwnedDialog(dialog)) return;

        if (dialog.ApiDefName != existing.Name)
            _editor.RenameEntity(apiDefId, EntityTypes.ApiDef, dialog.ApiDefName);

        _editor.UpdateApiDefProperties(apiDefId, dialog.IsPush, dialog.TxWorkId, dialog.RxWorkId, dialog.Duration, dialog.Description);

        if (IsSystemSelected && SelectedNode?.Id == systemId)
            RefreshSystemPanel(systemId);

        StatusText = $"ApiDef '{dialog.ApiDefName}' updated.";
    }
}
