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
        _stateCache.Clear();
        if (_simEngine is null) return;

        foreach (var entry in EnumerateSimulationEntries())
        {
            AddSimNode(entry);
            _stateCache.Set(entry.Id, Status4.Ready);
        }
        PopulateWorkItems();
    }

    private void PopulateWorkItems()
    {
        SimWorkItems.Clear();
        SelectedSimWork = null;
        if (_simEngine is null) return;

        var activeSystemNames = _simEngine.Index.ActiveSystemNames;
        var sourceGuids = new HashSet<Guid>(_simEngine.Index.TokenSourceGuids);
        var sourceItems = new List<SimWorkItem>();
        var normalItems = new List<SimWorkItem>();

        foreach (var entry in EnumerateSimulationEntries())
        {
            if (entry.Kind != EntityKind.Work || !activeSystemNames.Contains(entry.SystemName))
                continue;

            var item = new SimWorkItem(entry.Id, entry.Name);
            if (sourceGuids.Contains(entry.Id))
                sourceItems.Add(item);
            else
                normalItems.Add(item);
        }

        if (sourceItems.Count > 0)
        {
            SimWorkItems.Add(SimWorkItem.AutoStart);
            SimWorkItems.Add(SimWorkItem.SourceHeader);
            foreach (var item in sourceItems)
                SimWorkItems.Add(item);
        }

        if (normalItems.Count > 0)
        {
            SimWorkItems.Add(SimWorkItem.NormalHeader);
            foreach (var item in normalItems)
                SimWorkItems.Add(item);
        }

        var preferred = _lastSelectedWorkId is { } lastId
            ? SimWorkItems.FirstOrDefault(w => w.Guid == lastId)
            : null;

        SelectedSimWork = preferred
            ?? (sourceItems.Count > 0 ? SimWorkItem.AutoStart : null)
            ?? SimWorkItems.FirstOrDefault(w => w.Guid != Guid.Empty);
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
        foreach (var node in _canvasNodes)
        {
            node.SimState = null;
            node.SimTokenDisplay = "";
        }
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

    /// <summary>캔버스 노드가 새로 생성된 후 시뮬레이션 상태/토큰 뱃지를 복원합니다.</summary>
    internal void RestoreSimStateToCavas()
    {
        if (_simEngine is null || !IsSimulating) return;

        foreach (var node in _canvasNodes)
        {
            var cached = _stateCache.TryGet(node.Id);
            if (cached is not null)
                node.SimState = cached.Value;

            var tokenOpt = _simEngine.GetWorkToken(node.Id);
            if (tokenOpt is not null)
                node.SimTokenDisplay = FormatTokenDisplay(tokenOpt.Value);
        }
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
