using System;
using System.Linq;
using Ds2.Core;
using Ds2.UI.Core;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    private void InitSimNodes()
    {
        SimNodes.Clear();
        SimWorkItems.Clear();
        SelectedSimWork = null;
        _stateCache.Clear();
        if (_simEngine is null) return;

        var index = _simEngine.Index;
        foreach (var workGuid in index.AllWorkGuids)
        {
            var workName = index.WorkName.TryFind(workGuid);
            var systemName = index.WorkSystemName.TryFind(workGuid);
            if (workName == null || systemName == null) continue;

            AddSimNode(workGuid, workName.Value, "Work", systemName.Value);

            if (index.ActiveSystemNames.Contains(systemName.Value))
                SimWorkItems.Add(new SimWorkItem(workGuid, workName.Value));

            _stateCache.Set(workGuid, Status4.Ready);

            var callGuids = index.WorkCallGuids.TryFind(workGuid);
            if (callGuids == null) continue;

            foreach (var callGuid in callGuids.Value)
            {
                var call = DsQuery.getCall(callGuid, _store);
                if (call == null) continue;

                AddSimNode(callGuid, $"  - {call.Value.Name}", "Call", systemName.Value);
                _stateCache.Set(callGuid, Status4.Ready);
            }
        }
    }

    private void UpdateSimNodeState(Guid nodeGuid, Status4 newState)
    {
        var row = SimNodes.FirstOrDefault(node => node.NodeGuid == nodeGuid);
        if (row is not null) row.State = newState;

        var canvasNode = CanvasNodes.FirstOrDefault(node => node.Id == nodeGuid);
        if (canvasNode is not null) canvasNode.SimState = newState;
    }

    private void ApplySimStateToCanvas()
    {
        SetCanvasSimState(Status4.Ready, static node =>
            node.EntityType == EntityKind.Work || node.EntityType == EntityKind.Call);
    }

    private void ClearSimStateFromCanvas()
    {
        SetCanvasSimState(null, static _ => true);
    }

    private void AddSimNode(Guid nodeGuid, string name, string nodeType, string systemName)
    {
        SimNodes.Add(new SimNodeRow
        {
            NodeGuid = nodeGuid,
            Name = name,
            NodeType = nodeType,
            SystemName = systemName,
            State = Status4.Ready
        });
    }

    private void SetCanvasSimState(Status4? state, Func<EntityNode, bool> predicate)
    {
        foreach (var node in CanvasNodes)
        {
            if (predicate(node))
                node.SimState = state;
        }
    }
}
