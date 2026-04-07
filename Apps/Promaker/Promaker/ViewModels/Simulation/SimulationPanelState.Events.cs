using System;
using System.Linq;
using System.Threading;
using Ds2.Core;
using Ds2.Runtime.Sim.Engine;
using Ds2.Runtime.Sim.Model;
using Ds2.Core.Store;
using Ds2.Editor;

namespace Promaker.ViewModels;

public partial class SimulationPanelState
{
    private void WireSimEvents()
    {
        if (_simEngine is null) return;

        var engine = _simEngine;
        var generation = Interlocked.Read(ref _simUiGeneration);

        engine.WorkStateChanged += (_, args) =>
            _dispatcher.BeginInvoke(() =>
            {
                if (!ReferenceEquals(_simEngine, engine) || Interlocked.Read(ref _simUiGeneration) != generation)
                    return;
                OnWorkStateChanged(args);
            });

        engine.CallStateChanged += (_, args) =>
            _dispatcher.BeginInvoke(() =>
            {
                if (!ReferenceEquals(_simEngine, engine) || Interlocked.Read(ref _simUiGeneration) != generation)
                    return;
                OnCallStateChanged(args);
            });

        engine.SimulationStatusChanged += (_, args) =>
            _dispatcher.BeginInvoke(() =>
            {
                if (!ReferenceEquals(_simEngine, engine) || Interlocked.Read(ref _simUiGeneration) != generation)
                    return;
                OnSimStatusChanged(args);
            });

        WireTokenEvent(engine, generation);
    }

    private void OnWorkStateChanged(WorkStateChangedArgs args)
    {
        ApplyWorkStateChangeToVisibleNode(args);
        // 디버그: Homing/Ready 전이 로그 (리셋 동작 확인용)
        if (args.NewState == Status4.Homing || (args.PreviousState == Status4.Homing && args.NewState == Status4.Ready))
            AddSimLog($"[Reset] {args.WorkName}: {args.PreviousState} → {args.NewState}");

        _sceneEventHandler?.OnWorkStateChanged(args.WorkGuid, args.NewState);
        RefreshSimulationProgressUi();
    }

    private void OnCallStateChanged(CallStateChangedArgs args)
    {
        ApplyCallStateChangeToVisibleNode(args);
        SetSimSkipped(args.CallGuid, args.IsSkipped);

        _sceneEventHandler?.OnCallStateChanged(args.CallGuid, args.NewState);
        RefreshSimulationProgressUi();
    }

    private void ApplyCallStateChangeToVisibleNode(CallStateChangedArgs args)
    {
        var suffix = args.IsSkipped ? " (Skip)" : "";
        var systemName = GetSystemName(EntityKind.Call, args.CallGuid);
        var canonicalId = Queries.resolveOriginalCallId(args.CallGuid, Store);

        _stateCache.Set(canonicalId, args.NewState);
        UpdateSimNodeState(canonicalId, args.NewState);
        GanttChart.UpdateNodeState(canonicalId, args.NewState, GanttChart.AdjustedNow);

        RecordStateChange(args.CallGuid.ToString(), args.CallName + suffix, EntityKind.Call.ToString(), systemName, args.NewState);
        UpdateSimClock();
    }

    private void OnSimStatusChanged(SimulationStatusChangedArgs args)
    {
        if (args.NewStatus == SimulationStatus.Stopped)
        {
            GanttChart.IsRunning = false;
            IsSimulating = false;
            IsSimPaused = false;
            AddSimLog(SimText.Completed);
            UpdateSimClock();
        }
    }

    private void UpdateSimClock()
    {
        if (_simEngine is not null)
            SimClock = _simEngine.State.Clock.ToString(SimText.ClockFormat);
    }

    private string GetSystemName(EntityKind kind, Guid entityGuid)
    {
        if (_simEngine is null) return "";

        if (kind == EntityKind.Work)
        {
            var systemName = _simEngine.Index.WorkSystemName.TryFind(entityGuid);
            return systemName?.Value ?? "";
        }

        var workGuid = _simEngine.Index.CallWorkGuid.TryFind(entityGuid);
        if (workGuid == null) return "";

        var callSystemName = _simEngine.Index.WorkSystemName.TryFind(workGuid.Value);
        return callSystemName?.Value ?? "";
    }

    private void ApplyNodeStateChange(Guid nodeGuid, Status4 newState, string nodeName, EntityKind nodeKind, string systemName)
    {
        _stateCache.Set(nodeGuid, newState);
        UpdateSimNodeState(nodeGuid, newState);
        GanttChart.UpdateNodeState(nodeGuid, newState, GanttChart.AdjustedNow);
        RecordStateChange(nodeGuid.ToString(), nodeName, nodeKind.ToString(), systemName, newState);
        UpdateSimClock();
    }

    private void ApplyWorkStateChangeToVisibleNode(WorkStateChangedArgs args)
    {
        var systemName = GetSystemName(EntityKind.Work, args.WorkGuid);
        var canonicalId = Queries.resolveOriginalWorkId(args.WorkGuid, Store);

        _stateCache.Set(canonicalId, args.NewState);
        UpdateSimNodeState(canonicalId, args.NewState);
        GanttChart.UpdateNodeState(canonicalId, args.NewState, GanttChart.AdjustedNow);

        RecordStateChange(args.WorkGuid.ToString(), args.WorkName, EntityKind.Work.ToString(), systemName, args.NewState);
        UpdateSimClock();
    }
}
