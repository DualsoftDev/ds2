using System;
using System.Linq;
using System.Threading;
using Ds2.Core;
using Ds2.Runtime.Engine;
using Ds2.Runtime.Model;
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

        engine.CallTimeout += (_, args) =>
            _dispatcher.BeginInvoke(() =>
            {
                if (!ReferenceEquals(_simEngine, engine) || Interlocked.Read(ref _simUiGeneration) != generation)
                    return;
                OnCallTimeout(args);
            });

        // v12 P5 — 경로이탈 이상감지. proxy 모드면 Agent engine 이 단일 발행한 OnAbnormal 을 받아 재발행한 것.
        engine.AbnormalDetected += (_, record) =>
            _dispatcher.BeginInvoke(() =>
            {
                if (!ReferenceEquals(_simEngine, engine) || Interlocked.Read(ref _simUiGeneration) != generation)
                    return;
                OnAbnormalDetected(record);
            });

        WireTokenEvent(engine, generation);
    }

    private void OnCallTimeout(CallTimeoutArgs args)
    {
        _warningGuids.Add(args.CallGuid);
        ApplyWarningsToCanvas();
        AddWarningLog("TIMEOUT", $"{args.CallName} Timeout ({args.TimeoutMs}ms)");
        SimLog.Warn($"[Timeout] {args.CallName} ({args.TimeoutMs}ms) @{args.Clock}");
    }

    /// <summary>v12 P5 — 경로이탈 이상감지 표시(최소). SensorOpen/SensorShort/ActionOver/ActionUnder.
    /// Action* 는 elapsedMs 동반, Sensor* 는 -1. 캔버스 하이라이트 등 상세 UI 는 P6.</summary>
    private void OnAbnormalDetected(AbnormalRecord record)
    {
        var elapsed = Microsoft.FSharp.Core.FSharpOption<int>.get_IsSome(record.ElapsedMs)
            ? record.ElapsedMs.Value : -1;
        var detail = elapsed >= 0 ? $"{record.Kind} (elapsed={elapsed}ms)" : record.Kind.ToString();
        AddWarningLog("ABNORMAL", detail);
        SimLog.Warn($"[Abnormal] {record.Kind} elapsed={elapsed} @{record.TimestampUtc:HH:mm:ss}");
    }

    private static LogSeverity SeverityFromState(Status4 state) => state switch
    {
        Status4.Ready => LogSeverity.Ready,
        Status4.Going => LogSeverity.Going,
        Status4.Finish => LogSeverity.Finish,
        Status4.Homing => LogSeverity.Homing,
        _ => LogSeverity.Info
    };

    private void OnWorkStateChanged(WorkStateChangedArgs args)
    {
        ApplyWorkStateChangeToVisibleNode(args);
#if DEBUG
        AddSimLog($"W {args.WorkName}: {args.PreviousState}→{args.NewState}", SeverityFromState(args.NewState));
#endif
        _sceneEventHandler?.OnWorkStateChanged(args.WorkGuid, args.NewState);
        RefreshSimulationProgressUi();
        ContinuousInjection.TryContinue(args.WorkGuid, args.NewState);
        NotifyRuntimeIoChanged();
    }

    private void OnCallStateChanged(CallStateChangedArgs args)
    {
        ApplyCallStateChangeToVisibleNode(args);
#if DEBUG
        var skip = args.IsSkipped ? " (Skip)" : "";
        AddSimLog($"C {args.CallName}: {args.PreviousState}→{args.NewState}{skip}", SeverityFromState(args.NewState));
#endif
        SetSimSkipped(args.CallGuid, args.IsSkipped);

        _sceneEventHandler?.OnCallStateChanged(args.CallGuid, args.NewState);
        RefreshSimulationProgressUi();
        NotifyRuntimeIoChanged();
    }

    private void ApplyCallStateChangeToVisibleNode(CallStateChangedArgs args)
    {
        var suffix = args.IsSkipped ? " (Skip)" : "";
        var systemName = GetSystemName(EntityKind.Call, args.CallGuid);
        var canonicalId = Queries.resolveOriginalCallId(args.CallGuid, Store);
        var timestamp = ResolveEventTimestamp(args.Clock);

        _stateCache.Set(canonicalId, args.NewState);
        UpdateSimNodeState(canonicalId, args.NewState);
        GanttChart.UpdateNodeState(canonicalId, args.NewState, timestamp);

        Report.RecordStateChange(args.CallGuid.ToString(), args.CallName + suffix, EntityKind.Call.ToString(), systemName, args.NewState);
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
            // Stopped 시점엔 SimEngine 이 곧 disposed 되므로 명시적으로 null 스냅샷 전달.
            RuntimeIoChanged?.Invoke(null);
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
        var timestamp = CurrentGanttTimestamp();

        _stateCache.Set(nodeGuid, newState);
        UpdateSimNodeState(nodeGuid, newState);
        GanttChart.UpdateNodeState(nodeGuid, newState, timestamp);
        Report.RecordStateChange(nodeGuid.ToString(), nodeName, nodeKind.ToString(), systemName, newState);
        UpdateSimClock();
    }

    private void ApplyWorkStateChangeToVisibleNode(WorkStateChangedArgs args)
    {
        var systemName = GetSystemName(EntityKind.Work, args.WorkGuid);
        var canonicalId = Queries.resolveOriginalWorkId(args.WorkGuid, Store);
        var timestamp = ResolveEventTimestamp(args.Clock);

        _stateCache.Set(canonicalId, args.NewState);
        UpdateSimNodeState(canonicalId, args.NewState);
        GanttChart.UpdateNodeState(canonicalId, args.NewState, timestamp);

        Report.RecordStateChange(args.WorkGuid.ToString(), args.WorkName, EntityKind.Work.ToString(), systemName, args.NewState);
        UpdateSimClock();
    }

    private DateTime ToGanttTimestamp(TimeSpan clock) => ResolveGanttEventTimestamp(_simStartTime, clock);

    internal static DateTime ResolveGanttEventTimestamp(DateTime simStartTime, TimeSpan clock) =>
        simStartTime + clock;

    internal static TimeSpan ResolvePassiveGanttElapsed(TimeSpan clock, TimeSpan anchor) =>
        clock >= anchor ? clock - anchor : TimeSpan.Zero;

    internal static bool UsesSignalDrivenGanttTimeline(RuntimeMode mode) =>
        mode == RuntimeMode.VirtualPlant
        || mode == RuntimeMode.Monitoring;

    internal static DateTime ResolveSignalDrivenGanttNow(
        DateTime simStartTime,
        TimeSpan? anchor,
        TimeSpan baseElapsed,
        DateTime baseWall,
        DateTime now)
    {
        if (anchor is null)
            return simStartTime;

        var wallElapsed = now >= baseWall ? now - baseWall : TimeSpan.Zero;
        return simStartTime + baseElapsed + wallElapsed;
    }

    internal static TimeSpan ResolvePassiveEventBaseElapsed(TimeSpan eventElapsed, TimeSpan estimatedElapsed) =>
        eventElapsed > estimatedElapsed ? eventElapsed : estimatedElapsed;

    private bool IsSignalDrivenGanttTimeline =>
        UsesSignalDrivenGanttTimeline(SelectedRuntimeMode);

    private void ResetPassiveGanttClockAnchor()
    {
        _passiveGanttClockAnchor = null;
        _passiveGanttBaseWall = DateTime.Now;
        _passiveGanttBaseElapsed = TimeSpan.Zero;
    }

    private TimeSpan EstimatePassiveGanttElapsed(DateTime now)
    {
        if (_passiveGanttClockAnchor is null)
            return TimeSpan.Zero;

        var wallElapsed = now >= _passiveGanttBaseWall ? now - _passiveGanttBaseWall : TimeSpan.Zero;
        return _passiveGanttBaseElapsed + wallElapsed;
    }

    private void AdvancePassiveGanttBase(TimeSpan eventElapsed)
    {
        var now = DateTime.Now;
        var estimatedElapsed = EstimatePassiveGanttElapsed(now);
        _passiveGanttBaseElapsed = ResolvePassiveEventBaseElapsed(eventElapsed, estimatedElapsed);
        _passiveGanttBaseWall = now;
    }

    private TimeSpan ResolveDisplayClock(TimeSpan clock)
    {
        if (!IsSignalDrivenGanttTimeline)
            return clock;

        if (_passiveGanttClockAnchor is null)
        {
            _passiveGanttClockAnchor = clock;
            _passiveGanttBaseElapsed = TimeSpan.Zero;
            _passiveGanttBaseWall = DateTime.Now;
            return TimeSpan.Zero;
        }

        var elapsed = ResolvePassiveGanttElapsed(clock, _passiveGanttClockAnchor.Value);
        AdvancePassiveGanttBase(elapsed);
        return elapsed;
    }

    private DateTime ResolveSignalDrivenGanttNow() =>
        ResolveSignalDrivenGanttNow(
            _simStartTime,
            _passiveGanttClockAnchor,
            _passiveGanttBaseElapsed,
            _passiveGanttBaseWall,
            DateTime.Now);

    /// <summary>
    /// Engine event clock is the source of truth for persisted Gantt segments in every runtime mode.
    /// Control/VP events can be marshaled to the UI dispatcher in a burst after the real 500ms delay already
    /// elapsed; using AdjustedNow at dispatch time collapses those segments into near-zero-width bars.
    /// VP/Monitoring additionally anchor their display clock at the first accepted signal so PLAY order does not
    /// add idle lead time before Ctrl/PLC starts broadcasting.
    /// </summary>
    private DateTime ResolveEventTimestamp(TimeSpan clock) => ToGanttTimestamp(ResolveDisplayClock(clock));

    private DateTime CurrentGanttTimestamp() =>
        IsSignalDrivenGanttTimeline
            ? _passiveGanttClockAnchor is null || _simEngine is null
                ? _simStartTime
                : ToGanttTimestamp(ResolvePassiveGanttElapsed(_simEngine.State.Clock, _passiveGanttClockAnchor.Value))
            : SelectedRuntimeMode != RuntimeMode.Simulation
                ? GanttChart.AdjustedNow
                : _simEngine is null ? GanttChart.AdjustedNow : ToGanttTimestamp(_simEngine.State.Clock);
}
