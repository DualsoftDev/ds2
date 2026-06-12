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
        // 통신 blackout — 두절 구간의 abnormal 은 증거가 아니라 신호 부재의 산물 (backend 와 동형 억제).
        if (_commBlackout)
        {
            SimLog.Info($"[CommBlackout] abnormal suppressed: {record.Kind}");
            return;
        }

        // 학습 오염 차단 — abnormal 사이클의 진행 중 duration 측정은 폐기 (기준선이 비정상을 따라가면 안 됨).
        if (_durationLearning is { } learning)
        {
            if (Microsoft.FSharp.Core.FSharpOption<Guid>.get_IsSome(record.Target.CallId))
            {
                var callId = record.Target.CallId.Value;
                learning.Invalidate(callId);
                // abnormal Call 이 속한 Active Work 의 이번 사이클 측정도 폐기.
                var call = OptionValue(Queries.getCall(callId, Store));
                if (call is not null)
                    learning.InvalidateWork(call.ParentId);
            }
            if (Microsoft.FSharp.Core.FSharpOption<Guid>.get_IsSome(record.Target.WorkId))
                learning.InvalidateWork(record.Target.WorkId.Value);
        }

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
        // Active Work 자체 실측 학습 — device 합산에 안 잡히는 단계 간 전환 갭 포함 전체 사이클.
        _durationLearning?.OnWorkStateChanged(args.WorkGuid, args.NewState, args.Clock.TotalMilliseconds);
        // 라이브 반영(사이클 경계마다 store 갱신 + ReloadDurations)은 2회 실측에서 라인 정지를
        // 유발해 제거 — 동작 중 ReloadDurations 가 엔진 전이와 race(In 신호 미아) 하는 것으로 추정.
        // 엔진의 동작 중 Reload 안전성이 규명되기 전까지 학습 반영은 정지 시에만.
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
        // 실측 duration 학습 — raw engine clock 으로 Going→Finish 구간 측정 (간트 표시 시계와 무관).
        _durationLearning?.OnCallStateChanged(args.CallGuid, args.NewState, args.Clock.TotalMilliseconds);
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

    /// <summary>
    /// 간트 시간축을 "첫 신호 anchor 기준"으로 둘지 여부.
    /// VP/Monitoring 은 외부 신호 owner 라 항상 anchor.
    /// Control+실PLC(UsesAgentProxy) 는 Agent engine clock 의 원점이 WPF Start 가 아니라 Agent 시작 시점.
    /// self-hosted Control 도 PLAY 처리에서 _simStartTime 설정(간트 원점) 과 engine.Start() 사이에
    /// Hub 스냅샷 동기 대기(최대 ~8초) 가 끼므로 raw clock 은 그 소요시간만큼 wall 대비 과거로 어긋난다
    /// — 진행 바가 빨간선까지 늘어지다 전이 때 과거 위치로 챡 붙는 왜곡의 원인.
    /// → Simulation(보간 시계가 원점 공유) 을 제외한 모든 모드를 anchor 로 첫 이벤트 기준 0 정렬한다.
    /// </summary>
    internal static bool UsesAnchoredGanttTimeline(RuntimeMode mode) =>
        mode != RuntimeMode.Simulation;

    private bool IsSignalDrivenGanttTimeline =>
        UsesAnchoredGanttTimeline(SelectedRuntimeMode);

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
