using System;
using Ds2.Core;
using Ds2.Runtime.Sim.Engine;
using Ds2.Runtime.Sim.Model;
using Ds2.Store;
using Ds2.Editor;

namespace Promaker.ViewModels;

public partial class SimulationPanelState
{
    private void WireSimEvents()
    {
        if (_simEngine is null) return;

        _simEngine.WorkStateChanged += (_, args) =>
            _dispatcher.BeginInvoke(() => OnWorkStateChanged(args));

        _simEngine.CallStateChanged += (_, args) =>
            _dispatcher.BeginInvoke(() => OnCallStateChanged(args));

        _simEngine.SimulationStatusChanged += (_, args) =>
            _dispatcher.BeginInvoke(() => OnSimStatusChanged(args));

        WireTokenEvent();
    }

    private void OnWorkStateChanged(WorkStateChangedArgs args)
    {
        ApplyNodeStateChange(args.WorkGuid, args.NewState, args.WorkName, EntityKind.Work, GetSystemName(EntityKind.Work, args.WorkGuid));
        // 디버그: Homing/Ready 전이 로그 (리셋 동작 확인용)
        if (args.NewState == Status4.Homing || (args.PreviousState == Status4.Homing && args.NewState == Status4.Ready))
            AddSimLog($"[Reset] {args.WorkName}: {args.PreviousState} → {args.NewState}");
    }

    private void OnCallStateChanged(CallStateChangedArgs args)
    {
        var suffix = args.IsSkipped ? " (Skip)" : "";
        ApplyNodeStateChange(args.CallGuid, args.NewState, args.CallName + suffix, EntityKind.Call, GetSystemName(EntityKind.Call, args.CallGuid));
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
            SimClock = _simEngine.State.Clock.ToString(@"hh\:mm\:ss\.fff");
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
}
