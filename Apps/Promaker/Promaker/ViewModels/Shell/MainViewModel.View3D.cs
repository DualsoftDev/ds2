using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Ds2.View3D;
using Microsoft.FSharp.Core;
using Promaker.Windows;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    [RelayCommand(CanExecute = nameof(CanOpen3DView))]
    private void Open3DView()
    {
        if (_view3DWindow is { IsVisible: true })
        {
            _view3DWindow.Activate();
            return;
        }

        var store = _store;
        var projectId = Queries.allProjects(_store).Head.Id;
        _view3DWindow = new View3DWindow(Simulation.ThreeD,
            onReady: () => Simulation.ThreeD.BuildScene(store, projectId));
        _view3DWindow.SetSceneData(store, projectId, _currentFilePath);

        Simulation.ThreeD.SetSelectionCallbacks(
            onDeviceSelected: (systemId, kind) => Handle3DDeviceSelection(systemId, kind),
            onApiDefSelected: (deviceId, apiName) => Handle3DApiDefSelection(deviceId, apiName),
            onEmptySpaceSelected: Handle3DEmptySpaceSelection,
            onDeviceInfoRequested: (deviceName, deviceData) => _view3DWindow.ShowDeviceInfo(deviceName, deviceData)
        );

        _view3DWindow.Owner = Application.Current.MainWindow;
        _view3DWindow.Show();
    }

    private void ResyncView3DIfOpen()
    {
        if (_view3DWindow is not { IsVisible: true })
            return;

        var projects = Queries.allProjects(_store);
        if (projects.IsEmpty)
            return;

        var projectId = projects.Head.Id;
        _view3DWindow.SetSceneData(_store, projectId, _currentFilePath);
        _ = Simulation.ThreeD.BuildScene(_store, projectId);
    }

    private void Handle3DDeviceSelection(Guid systemId, EntityKind kind)
    {
        var systemNode = FindNodeById(systemId, kind);
        if (systemNode != null)
        {
            SelectedNode = systemNode;
            PropertyPanel.SyncSelection(systemNode, [new SelectionKey(systemId, kind)]);
        }

        _view3DWindow?.SelectDeviceInTree(systemId);
    }

    private void Handle3DApiDefSelection(Guid deviceId, string apiName)
    {
        try
        {
            if (!_store.Systems.TryGetValue(deviceId, out var system))
                return;

            var targetApiDef = _store.ApiDefs.Values
                .FirstOrDefault(ad => ad.ParentId == deviceId
                    && ad.Name.Equals(apiName, StringComparison.OrdinalIgnoreCase));
            if (targetApiDef == null)
                return;

            var matchingCalls = _store.Calls.Values
                .Where(c => c.ApiCalls.Any(ac =>
                    FSharpOption<Guid>.get_IsSome(ac.ApiDefId) && ac.ApiDefId.Value == targetApiDef.Id))
                .ToList();

            var outgoing3D = new List<object>();
            var incoming3D = new List<object>();
            var outgoingItems = new List<ConnectionItem>();
            var incomingItems = new List<ConnectionItem>();

            foreach (var call in matchingCalls)
            {
                var arrows = Queries.arrowCallsOf(call.ParentId, _store);

                foreach (var arrow in arrows.Where(a => a.TargetId == call.Id))
                {
                    if (!TryResolveCallToSystemViaApiDef(arrow.SourceId, out var srcSystemId, out var srcApiName))
                        continue;

                    incoming3D.Add(new { deviceId = srcSystemId.ToString(), apiDefName = srcApiName });
                    var srcSys = _store.Systems.GetValueOrDefault(srcSystemId);
                    incomingItems.Add(new ConnectionItem(srcSys?.Name ?? "?", srcApiName ?? "", "←"));
                }

                foreach (var arrow in arrows.Where(a => a.SourceId == call.Id))
                {
                    if (!TryResolveCallToSystemViaApiDef(arrow.TargetId, out var tgtSystemId, out var tgtApiName))
                        continue;

                    outgoing3D.Add(new { deviceId = tgtSystemId.ToString(), apiDefName = tgtApiName });
                    var tgtSys = _store.Systems.GetValueOrDefault(tgtSystemId);
                    outgoingItems.Add(new ConnectionItem(tgtSys?.Name ?? "?", tgtApiName ?? "", "→"));
                }
            }

            _ = Simulation.ThreeD.ShowApiDefConnections(deviceId, apiName, outgoing3D, incoming3D);
            _view3DWindow?.ShowConnectionInfo($"{system.Name}.{apiName}", outgoingItems, incomingItems);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[3D] ApiDef selection error: {ex.Message}");
        }
    }

    private bool TryResolveCallToSystemViaApiDef(Guid callId, out Guid systemId, out string? apiName)
    {
        systemId = Guid.Empty;
        apiName = null;
        if (!_store.Calls.TryGetValue(callId, out var call))
            return false;

        foreach (var ac in call.ApiCalls)
        {
            if (!FSharpOption<Guid>.get_IsSome(ac.ApiDefId))
                continue;
            if (!_store.ApiDefs.TryGetValue(ac.ApiDefId.Value, out var apiDef))
                continue;
            if (!_store.Systems.ContainsKey(apiDef.ParentId))
                continue;

            systemId = apiDef.ParentId;
            apiName = apiDef.Name;
            return true;
        }

        return false;
    }

    private void Handle3DEmptySpaceSelection()
    {
        Selection.Reset();
        PropertyPanel.SyncSelection(null, []);
    }

    private EntityNode? FindNodeById(Guid id, EntityKind kind)
    {
        return kind switch
        {
            EntityKind.System => DeviceTreeRoots.FirstOrDefault(n => n.Id == id),
            EntityKind.Work or EntityKind.Call => ControlTreeRoots
                .SelectMany(EnumerateAllDescendants)
                .FirstOrDefault(n => n.Id == id && n.EntityType == kind),
            _ => null
        };
    }

    private static IEnumerable<EntityNode> EnumerateAllDescendants(EntityNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in EnumerateAllDescendants(child))
                yield return descendant;
        }
    }
}
