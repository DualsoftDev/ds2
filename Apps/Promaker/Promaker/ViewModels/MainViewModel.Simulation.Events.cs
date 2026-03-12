using System;
using Ds2.Core;
using Ds2.Runtime.Sim.Engine;
using Ds2.Runtime.Sim.Model;

namespace Promaker.ViewModels;

public partial class MainViewModel
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
    }

    private void OnWorkStateChanged(WorkStateChangedArgs args)
    {
        ApplyNodeStateChange(args.WorkGuid, args.NewState, args.WorkName, "Work", GetWorkSystemName(args.WorkGuid));
    }

    private void OnCallStateChanged(CallStateChangedArgs args)
    {
        var suffix = args.IsSkipped ? " (Skip)" : "";
        ApplyNodeStateChange(args.CallGuid, args.NewState, args.CallName + suffix, "Call", GetCallSystemName(args.CallGuid));
    }

    private void OnSimStatusChanged(SimulationStatusChangedArgs args)
    {
        if (args.NewStatus == SimulationStatus.Stopped)
        {
            IsSimulating = false;
            IsSimPaused = false;
            AddSimLog("시뮬레이션 완료");
        }
    }

    private void UpdateSimClock()
    {
        if (_simEngine is not null)
            SimClock = _simEngine.State.Clock.ToString(@"hh\:mm\:ss\.fff");
    }

    private string GetWorkSystemName(Guid workGuid)
    {
        if (_simEngine is null) return "";
        var opt = _simEngine.Index.WorkSystemName.TryFind(workGuid);
        return opt != null ? opt.Value : "";
    }

    private string GetCallSystemName(Guid callGuid)
    {
        if (_simEngine is null) return "";
        var workOpt = _simEngine.Index.CallWorkGuid.TryFind(callGuid);
        if (workOpt == null) return "";
        var sysOpt = _simEngine.Index.WorkSystemName.TryFind(workOpt.Value);
        return sysOpt != null ? sysOpt.Value : "";
    }

    private void ApplyNodeStateChange(Guid nodeGuid, Status4 newState, string nodeName, string nodeType, string systemName)
    {
        _stateCache.Set(nodeGuid, newState);
        UpdateSimNodeState(nodeGuid, newState);
        RecordStateChange(nodeGuid.ToString(), nodeName, nodeType, systemName, newState);
        UpdateSimClock();
    }
}
