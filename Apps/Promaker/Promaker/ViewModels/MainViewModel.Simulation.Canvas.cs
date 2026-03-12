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

        var idx = _simEngine.Index;
        foreach (var workGuid in idx.AllWorkGuids)
        {
            var wName = idx.WorkName.TryFind(workGuid);
            var wSysName = idx.WorkSystemName.TryFind(workGuid);
            if (wName == null || wSysName == null) continue;

            AddSimNode(workGuid, wName.Value, "Work", wSysName.Value);

            if (idx.ActiveSystemNames.Contains(wSysName.Value))
                SimWorkItems.Add(new SimWorkItem(workGuid, wName.Value));

            _stateCache.Set(workGuid, Status4.Ready);

            var callGuids = idx.WorkCallGuids.TryFind(workGuid);
            if (callGuids != null)
            {
                foreach (var callGuid in callGuids.Value)
                {
                    var call = DsQuery.getCall(callGuid, _store);
                    if (call != null)
                    {
                        AddSimNode(callGuid, $"  ㄴ {call.Value.Name}", "Call", wSysName.Value);
                        _stateCache.Set(callGuid, Status4.Ready);
                    }
                }
            }
        }
    }

    private void UpdateSimNodeState(Guid nodeGuid, Status4 newState)
    {
        var row = SimNodes.FirstOrDefault(n => n.NodeGuid == nodeGuid);
        if (row is not null) row.State = newState;

        var canvasNode = CanvasNodes.FirstOrDefault(n => n.Id == nodeGuid);
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
