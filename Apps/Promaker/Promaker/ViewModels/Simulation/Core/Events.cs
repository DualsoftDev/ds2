using System;
using System.Collections.Generic;
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
        // 간트 경고색 — 판정된 Call 의 해당 사이클 바만 (RxWork=device Work 는 간트에 행이 없어 Call 단위가 전부).
        if (Microsoft.FSharp.Core.FSharpOption<Guid>.get_IsSome(record.Target.CallId))
            GanttChart.MarkAbnormal(Queries.resolveOriginalCallId(record.Target.CallId.Value, Store));

        var elapsed = Microsoft.FSharp.Core.FSharpOption<int>.get_IsSome(record.ElapsedMs)
            ? record.ElapsedMs.Value : -1;
        var target = FormatAbnormalTarget(record.Target);
        var detail = elapsed >= 0
            ? $"{record.Kind} {target} (elapsed={elapsed}ms)"
            : $"{record.Kind} {target}";
        AddWarningLog("ABNORMAL", detail);
        SimLog.Warn($"[Abnormal] {record.Kind} {target} elapsed={elapsed} @{record.TimestampUtc:HH:mm:ss}");
    }

    private string FormatAbnormalTarget(AbnormalTarget target)
    {
        var store = Store;
        var parts = new List<string>(capacity: 4);

        var call = ResolveTargetCall(store, target);
        if (Microsoft.FSharp.Core.FSharpOption<Guid>.get_IsSome(target.CallId))
            parts.Add(FormatNamedId("Call", call?.Name, target.CallId.Value));

        if (call is not null)
        {
            var ownerWork = OptionValue(Queries.getWork(call.ParentId, store));
            parts.Add(FormatNamedId("OwnerWork", ownerWork?.Name, call.ParentId));
        }

        if (Microsoft.FSharp.Core.FSharpOption<Guid>.get_IsSome(target.ApiCallId))
        {
            var apiCallId = target.ApiCallId.Value;
            var apiCall = call?.ApiCalls.FirstOrDefault(api => api.Id == apiCallId)
                ?? FindApiCallById(store, apiCallId);
            parts.Add(FormatNamedId("ApiCall", apiCall?.Name, apiCallId));
        }

        if (Microsoft.FSharp.Core.FSharpOption<Guid>.get_IsSome(target.WorkId))
        {
            var workId = target.WorkId.Value;
            var work = OptionValue(Queries.getWork(workId, store));
            parts.Add(FormatNamedId("RxWork", work?.Name, workId));
        }

        return parts.Count == 0 ? "Target=unknown" : string.Join(" ", parts);
    }

    private static Call? ResolveTargetCall(DsStore store, AbnormalTarget target)
    {
        if (!Microsoft.FSharp.Core.FSharpOption<Guid>.get_IsSome(target.CallId))
            return null;

        var callId = target.CallId.Value;
        var call = OptionValue(Queries.getCall(callId, store));
        if (call is not null)
            return call;

        var canonicalId = Queries.resolveOriginalCallId(callId, store);
        return canonicalId == callId ? null : OptionValue(Queries.getCall(canonicalId, store));
    }

    private static ApiCall? FindApiCallById(DsStore store, Guid apiCallId)
    {
        foreach (var call in store.Calls.Values)
        {
            var apiCall = call.ApiCalls.FirstOrDefault(api => api.Id == apiCallId);
            if (apiCall is not null)
                return apiCall;
        }

        return null;
    }

    private static string FormatNamedId(string label, string? name, Guid id)
    {
        var resolved = string.IsNullOrWhiteSpace(name) ? "<missing>" : name;
        return $"{label}={resolved}#{ShortGuid(id)}";
    }

    private static T? OptionValue<T>(Microsoft.FSharp.Core.FSharpOption<T> option)
        where T : class =>
        Microsoft.FSharp.Core.FSharpOption<T>.get_IsSome(option) ? option.Value : null;

    private static string ShortGuid(Guid id)
    {
        var text = id.ToString("N");
        return text[..8];
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
        AddSimLog($"W {args.WorkName}: {args.PreviousState}→{args.NewState} @{args.Clock}", SeverityFromState(args.NewState));
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
        AddSimLog($"C {args.CallName}: {args.PreviousState}→{args.NewState}{skip} @{args.Clock}", SeverityFromState(args.NewState));
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

    /// <summary>엔진 이벤트 clock 이 간트 구간의 시간 원천이다 — dispatcher 로 옮겨진 뒤 AdjustedNow 를 쓰면
    /// burst dispatch 때 구간이 0 너비로 붕괴한다.</summary>
    private DateTime ResolveEventTimestamp(TimeSpan clock) => ToGanttTimestamp(clock);

    private DateTime CurrentGanttTimestamp() =>
        _simEngine is null ? GanttChart.AdjustedNow : ToGanttTimestamp(_simEngine.State.Clock);
}
