using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.FSharp.Core;
using Ds2.Core;
using Ds2.Runtime.Engine.Core;
using Ds2.Core.Store;
using Ds2.Editor;

namespace Promaker.ViewModels;

public partial class SimulationPanelState
{
    private Guid? _lastSelectedWorkId;

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
        var sourceItems = new List<SimWorkItem>();
        var normalItems = new List<SimWorkItem>();

        foreach (var entry in EnumerateSimulationEntries())
        {
            if (entry.Kind != EntityKind.Work || !activeSystemNames.Contains(entry.SystemName))
                continue;

            var item = new SimWorkItem(entry.Id, entry.Name);
            if (SimIndexModule.isTokenSource(_simEngine.Index, entry.Id))
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

        foreach (var canvasNode in _allCanvasNodes())
        {
            if (GetSimulationStateTargetId(canvasNode) == nodeGuid)
                canvasNode.SimState = newState;
        }
    }

    private static Guid GetSimulationStateTargetId(EntityNode node) =>
        node.ReferenceOfId ?? node.Id;

    private void ApplySimStateToCanvas()
    {
        SetCanvasSimState(Status4.Ready, static node =>
            node.EntityType == EntityKind.Work || node.EntityType == EntityKind.Call);
    }

    private void SyncSimulationStateFromEngine()
    {
        if (_simEngine is null || !IsSimulating) return;

        var timestamp = GanttChart.AdjustedNow;

        foreach (var entry in EnumerateSimulationEntries())
        {
            FSharpOption<Status4> state = entry.Kind switch
            {
                EntityKind.Work => _simEngine.GetWorkState(entry.Id),
                EntityKind.Call => _simEngine.GetCallState(entry.Id),
                _ => FSharpOption<Status4>.None
            };

            if (state is null)
                continue;

            var currentState = state.Value;

            _stateCache.Set(entry.Id, currentState);
            UpdateSimNodeState(entry.Id, currentState);
            GanttChart.SyncNodeState(entry.Id, currentState, timestamp);

            if (entry.Kind == EntityKind.Work)
                UpdateSimNodeToken(entry.Id);
        }
    }

    private void SetSimSkipped(Guid nodeGuid, bool isSkipped)
    {
        foreach (var canvasNode in _allCanvasNodes())
        {
            if (GetSimulationStateTargetId(canvasNode) == nodeGuid)
                canvasNode.IsSimSkipped = isSkipped;
        }
    }

    private void ClearSimStateFromCanvas()
    {
        foreach (var node in _allCanvasNodes())
        {
            node.SimState = null;
            node.IsSimSkipped = false;
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
    internal void RestoreSimStateToCanvas()
    {
        ApplyWarningsToCanvas();

        if (_simEngine is null || !IsSimulating) return;

        var skippedCalls = _simEngine.State.SkippedCalls;
        foreach (var node in _allCanvasNodes())
        {
            var targetId = GetSimulationStateTargetId(node);
            var cached = _stateCache.TryGet(targetId);
            if (cached is not null)
                node.SimState = cached.Value;

            node.IsSimSkipped = skippedCalls.Contains(targetId);

            var tokenOpt = _simEngine.GetWorkToken(targetId);
            if (tokenOpt is not null)
                node.SimTokenDisplay = FormatTokenDisplay(tokenOpt.Value);
        }
    }

    private void ApplyWarningsToCanvas()
    {
        foreach (var node in _allCanvasNodes())
            node.IsWarning = _warningGuids.Contains(node.Id);
        foreach (var node in _allTreeNodes())
            node.IsWarning = _warningGuids.Contains(node.Id);
    }

    /// <summary>노드 클릭 시 해당 노드의 경고를 해제합니다.</summary>
    internal void ClearWarning(Guid nodeId)
    {
        if (!_warningGuids.Remove(nodeId)) return;
        foreach (var node in _allCanvasNodes().Concat(_allTreeNodes()))
        {
            if (node.Id == nodeId)
                node.IsWarning = false;
        }
    }

    internal void ClearAllWarnings()
    {
        _warningGuids.Clear();
        foreach (var node in _allCanvasNodes().Concat(_allTreeNodes()))
            node.IsWarning = false;
    }

    private void SetCanvasSimState(Status4? state, Func<EntityNode, bool> predicate)
    {
        foreach (var node in _allCanvasNodes())
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
            // Reference Work는 간트에서 제외 (원본에서 동일 정보 표시)
            var canonical = index.WorkCanonicalGuids.TryFind(workGuid);
            if (canonical != null && canonical.Value != workGuid)
                continue;

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
                // Reference Call은 간트에서 제외 (원본에서 동일 정보 표시)
                var callCanonical = index.CallCanonicalGuids.TryFind(callGuid);
                if (callCanonical != null && callCanonical.Value != callGuid)
                    continue;

                var call = Queries.getCall(callGuid, Store);
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
