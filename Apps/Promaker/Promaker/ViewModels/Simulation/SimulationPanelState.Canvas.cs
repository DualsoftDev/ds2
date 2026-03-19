using System;
using System.Collections.Generic;
using System.Linq;
using Ds2.Core;
using Ds2.UI.Core;

namespace Promaker.ViewModels;

public partial class SimulationPanelState
{
    private static Guid? _lastSelectedWorkId;

    private void InitSimNodes()
    {
        SimNodes.Clear();
        SimWorkItems.Clear();
        SelectedSimWork = null;
        _stateCache.Clear();
        if (_simEngine is null) return;

        var activeSystemNames = _simEngine.Index.ActiveSystemNames;
        foreach (var entry in EnumerateSimulationEntries())
        {
            AddSimNode(entry);
            _stateCache.Set(entry.Id, Status4.Ready);

            if (entry.Kind == EntityKind.Work && activeSystemNames.Contains(entry.SystemName))
                SimWorkItems.Add(new SimWorkItem(entry.Id, entry.Name));
        }

        SelectedSimWork = (_lastSelectedWorkId is { } lastId
            ? SimWorkItems.FirstOrDefault(w => w.Guid == lastId)
            : null) ?? SimWorkItems.FirstOrDefault();
    }

    private void UpdateSimNodeState(Guid nodeGuid, Status4 newState)
    {
        var row = SimNodes.FirstOrDefault(node => node.NodeGuid == nodeGuid);
        if (row is not null) row.State = newState;

        var canvasNode = _canvasNodes.FirstOrDefault(node => node.Id == nodeGuid);
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

    private void AddSimNode(SimIndexedEntry entry)
    {
        SimNodes.Add(new SimNodeRow
        {
            NodeGuid = entry.Id,
            Name = entry.Kind == EntityKind.Call ? $"  - {entry.Name}" : entry.Name,
            NodeType = entry.Kind.ToString(),
            SystemName = entry.SystemName,
            State = Status4.Ready
        });
    }

    private void SetCanvasSimState(Status4? state, Func<EntityNode, bool> predicate)
    {
        foreach (var node in _canvasNodes)
        {
            if (predicate(node))
                node.SimState = state;
        }
    }

    private void InitGanttEntries()
    {
        foreach (var entry in EnumerateSimulationEntries())
            GanttChart.AddEntry(entry.Id, entry.Name, entry.Kind, entry.ParentWorkId, entry.SystemName);
    }

    private IEnumerable<SimIndexedEntry> EnumerateSimulationEntries()
    {
        if (_simEngine is null) yield break;

        var index = _simEngine.Index;
        var activeSystemNames = index.ActiveSystemNames;
        foreach (var workGuid in index.AllWorkGuids)
        {
            var workName = index.WorkName.TryFind(workGuid);
            var systemName = index.WorkSystemName.TryFind(workGuid);
            if (workName == null || systemName == null) continue;
            // Device System(ADV, RET 등)의 Work/Call은 Gantt/SimNode에서 제외
            if (!activeSystemNames.Contains(systemName.Value)) continue;

            yield return new SimIndexedEntry(workGuid, workName.Value, EntityKind.Work, systemName.Value);

            var callGuids = index.WorkCallGuids.TryFind(workGuid);
            if (callGuids == null) continue;

            foreach (var callGuid in callGuids.Value)
            {
                var call = DsQuery.getCall(callGuid, Store);
                if (call == null) continue;

                yield return new SimIndexedEntry(callGuid, call.Value.Name, EntityKind.Call, systemName.Value, workGuid);
            }
        }
    }

    private readonly record struct SimIndexedEntry(
        Guid Id,
        string Name,
        EntityKind Kind,
        string SystemName,
        Guid? ParentWorkId = null);
}
