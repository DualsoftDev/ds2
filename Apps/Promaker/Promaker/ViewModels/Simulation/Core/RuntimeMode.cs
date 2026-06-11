using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ds2.Backend.Common;
using Ds2.Core;
using Ds2.Runtime.Engine;
using Ds2.Runtime.Engine.Core;
using Ds2.Runtime.Engine.Passive;
using Ds2.Runtime.IO;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.FSharp.Core;

namespace Promaker.ViewModels;

public partial class SimulationPanelState
{
    private RuntimeModeSession? _runtimeSession;
    private PassiveInferenceSession? _passiveInference;
    private readonly object _runtimeImmediateEffectLock = new();

    private void PreparePassiveModeIoInference()
    {
        if (_simEngine is null)
        {
            _passiveInference = null;
            return;
        }

        _passiveInference = new PassiveInferenceSession(_simEngine.Index, _simEngine.IOMap, SelectedRuntimeMode);
        DrainPassiveInferenceLogs();
    }

    private void ObserveAndInferPassiveState(string address, string value)
    {
        if (_simEngine is null || _passiveInference is null)
            return;

        var actions = _passiveInference.Observe(
            address,
            value,
            new Func<Guid, Status4>(GetWorkStateSafe),
            new Func<Guid, Status4>(GetCallStateSafe));

        // 진단 — 인퍼런스 관측·전이가 UI 로그에만 남아 파일에서 추적 불가했던 갭.
        // Going 누락 사이클의 "신호는 왔는데 액션이 없었는지 / 신호 자체가 안 왔는지" 를 파일로 판별.
        if (SimLog.IsDebugEnabled)
        {
            var applied = actions as ICollection<PassiveInferenceAction> ?? actions.ToList();
            SimLog.Debug($"[Infer] obs {address}={value} → {applied.Count} action(s)");
            ApplyPassiveInferenceActions(applied);
        }
        else
        {
            ApplyPassiveInferenceActions(actions);
        }

        DrainPassiveInferenceLogs();
    }

    private void BaselinePassiveState(string address, string value)
    {
        _passiveInference?.Baseline(address, value);
    }

    private void ObservePassiveSignalDirection(
        string address,
        string value,
        bool isOut,
        IEnumerable<SignalMapping> mappings)
    {
        if (_simEngine is null || _passiveInference is null)
            return;

        var actions = _passiveInference.ObserveDirection(
            address,
            value,
            isOut,
            mappings,
            new Func<Guid, Status4>(GetWorkStateSafe),
            new Func<Guid, Status4>(GetCallStateSafe));

        if (SimLog.IsDebugEnabled)
        {
            var applied = actions as ICollection<PassiveInferenceAction> ?? actions.ToList();
            SimLog.Debug($"[Infer] obs{(isOut ? "Out" : "In")} {address}={value} → {applied.Count} action(s)");
            ApplyPassiveInferenceActions(applied);
        }
        else
        {
            ApplyPassiveInferenceActions(actions);
        }
        DrainPassiveInferenceLogs();
    }

    private void ApplyPassiveInferenceActions(IEnumerable<PassiveInferenceAction> actions)
    {
        if (_simEngine is null)
            return;

        var scheduledStateChange = false;
        foreach (var action in actions)
        {
            switch (action.TargetKind)
            {
                case PassiveInferenceTarget.Work:
                    if (!IsMappedDeviceWork(action.TargetGuid) && GetWorkStateSafe(action.TargetGuid) != action.State)
                    {
                        _simEngine.ForceWorkState(action.TargetGuid, action.State);
                        scheduledStateChange = true;
                        if (SimLog.IsDebugEnabled)
                            SimLog.Debug($"[Infer] Work {ResolveInferName(PassiveInferenceTarget.Work, action.TargetGuid)} → {action.State}");
                    }
                    break;

                case PassiveInferenceTarget.Call:
                    if (GetCallStateSafe(action.TargetGuid) != action.State)
                    {
                        _simEngine.ForceCallState(action.TargetGuid, action.State);
                        scheduledStateChange = true;
                        if (SimLog.IsDebugEnabled)
                            SimLog.Debug($"[Infer] Call {ResolveInferName(PassiveInferenceTarget.Call, action.TargetGuid)} → {action.State}");
                    }
                    break;
            }
        }

        // backend observeAndInfer 의 drainCurrentTick 과 동일 — 유추 전이가 stale 시계로 stamp 되어
        // Monitoring 간트 Going 길이가 왜곡(늘어남/붕괴)되는 것 차단.
        if (scheduledStateChange)
            DrainEngineClockToWall(_simEngine);
    }

    private void DrainPassiveInferenceLogs()
    {
        if (_passiveInference is null)
            return;

        foreach (var log in _passiveInference.DrainLogs())
            AddSimLog(log.Message, MapPassiveInferenceLogSeverity(log.Kind));
    }

    private void ApplyRuntimeHubEffects(IEnumerable<RuntimeHubEffect> effects)
    {
        var engine = _simEngine;
        if (engine is null)
            return;

        var runtimeSource = ResolveRuntimeHubSource();
        var hubGeneration = Hub.CurrentGeneration;
        foreach (var batch in RuntimeHubEffectPipeline.Build(effects))
        {
            if (batch.DelayMs <= 0)
            {
                ApplyRuntimeHubEffectBatch(
                    engine,
                    runtimeSource,
                    hubGeneration,
                    batch.Effects,
                    batch.AwaitWrites,
                    batch.RequiresExclusiveImmediateLane);
                continue;
            }

            _ = Task.Run(async () =>
            {
                await Task.Delay(batch.DelayMs);
                ApplyRuntimeHubEffectBatch(
                    engine,
                    runtimeSource,
                    hubGeneration,
                    batch.Effects,
                    batch.AwaitWrites,
                    batch.RequiresExclusiveImmediateLane);
            });
        }
    }

    private void ApplyRuntimeHubEffectBatch(
        ISimulationEngine engine,
        string runtimeSource,
        int hubGeneration,
        IReadOnlyList<RuntimeHubEffect> effects,
        bool awaitWrites,
        bool requiresExclusiveImmediateLane)
    {
        if (!ReferenceEquals(_simEngine, engine) || !Hub.IsCurrentGeneration(hubGeneration))
            return;

        if (!requiresExclusiveImmediateLane)
        {
            foreach (var effect in effects)
                ApplyRuntimeHubEffect(engine, runtimeSource, hubGeneration, effect, awaitWrite: false);
            if (awaitWrites)
                FlushBatchSenderSynchronously(hubGeneration);
            return;
        }

        lock (_runtimeImmediateEffectLock)
        {
            foreach (var effect in effects)
                ApplyRuntimeHubEffect(engine, runtimeSource, hubGeneration, effect, awaitWrite: false);
            if (awaitWrites)
                FlushBatchSenderSynchronously(hubGeneration);
        }
    }

    /// <summary>
    /// awaitWrites=true 일 때 batch 끝에 호출 — pending 한 WriteTag 들이 모두 송신될 때까지 동기 대기.
    /// 기존 per-effect await 의 의미(쓰기 완료 후 다음 단계 진행)를 유지.
    /// </summary>
    private void FlushBatchSenderSynchronously(int hubGeneration)
    {
        var sender = Hub.BatchSender;
        if (sender is null || !Hub.IsCurrentGeneration(hubGeneration)) return;
        try { sender.FlushAsync().Wait(TimeSpan.FromSeconds(2)); }
        catch { /* best-effort */ }
    }

    private void ApplyRuntimeHubEffect(
        ISimulationEngine engine,
        string runtimeSource,
        int hubGeneration,
        RuntimeHubEffect effect,
        bool awaitWrite)
    {
        if (!ReferenceEquals(_simEngine, engine) || !Hub.IsCurrentGeneration(hubGeneration))
            return;

        switch (effect.Kind)
        {
            case RuntimeHubEffectKind.Log:
                _dispatcher.BeginInvoke(() =>
                    AddSimLog(effect.Message, MapRuntimeHubLogSeverity(effect.Severity)));
                return;

            case RuntimeHubEffectKind.InjectIoByAddress:
                engine.InjectIOValueByAddress(effect.Address, effect.Value);
                DrainEngineClockToWall(engine);
                return;

            case RuntimeHubEffectKind.ForceWorkState:
                if (effect.WorkGuid != Guid.Empty)
                {
                    engine.ForceWorkState(effect.WorkGuid, effect.State);
                    DrainEngineClockToWall(engine);
                }
                return;

            case RuntimeHubEffectKind.ForceWorkStateIfGoing:
                // Control 모드 IN=true 응답 전용. engine 내부 lock 안에서 atomic 으로
                // currentState=Going 일 때만 Force — Reset 흐름 도중 stale 응답이 Homing→Finish
                // 잘못 전이시키는 race 차단.
                if (effect.WorkGuid != Guid.Empty)
                {
                    engine.TryForceWorkStateIfGoing(effect.WorkGuid, effect.State);
                    DrainEngineClockToWall(engine);
                }
                return;

            case RuntimeHubEffectKind.ForceWorkStateIfReady:
                if (effect.WorkGuid != Guid.Empty)
                {
                    engine.TryForceWorkStateIfReady(effect.WorkGuid, effect.State);
                    DrainEngineClockToWall(engine);
                }
                return;

            case RuntimeHubEffectKind.WriteTag:
                if (Hub.Connection is not null
                    && Hub.IsCurrentConnection(hubGeneration, Hub.Connection)
                    && !string.IsNullOrEmpty(effect.Address))
                {
                    // Batch sender 가 짧은 윈도우 내 WriteTag 들을 묶어 1개 SignalR 프레임으로 송신.
                    Hub.BatchSender?.Enqueue(effect.Address, effect.Value, runtimeSource);
                }
                return;

            case RuntimeHubEffectKind.PassiveObserve:
                _dispatcher.BeginInvoke(() =>
                {
                    if (ReferenceEquals(_simEngine, engine) && Hub.IsCurrentGeneration(hubGeneration))
                        ObserveAndInferPassiveState(effect.Address, effect.Value);
                });
                return;

            case RuntimeHubEffectKind.PassiveBaseline:
                _dispatcher.BeginInvoke(() =>
                {
                    if (ReferenceEquals(_simEngine, engine) && Hub.IsCurrentGeneration(hubGeneration))
                        BaselinePassiveState(effect.Address, effect.Value);
                });
                return;
        }
    }

    /// <summary>
    /// 엔진 시계를 벽시계 타깃까지 advance — self-hosted Control/VP 에서 hub effect 가 forced transition 을
    /// 만들기 전에 호출. 안 하면 전이가 마지막 loop wake 시각(stale)으로 stamp 되어 간트 막대가
    /// 빨간선(wall clock) 뒤로 늘어지다 끝에 챡 붙는 왜곡이 생긴다. (backend drainCurrentTick 과 동일 패턴)
    /// </summary>
    private static void DrainEngineClockToWall(ISimulationEngine engine)
    {
        var before = engine.CurrentTimeMs;
        engine.AdvanceSimulationToRealTime();
        var jumped = engine.CurrentTimeMs - before;
        if (jumped > 500)
            SimLog.Info($"[ClockSync] sim clock jumped {jumped}ms on hub effect (stale stamp window)");
    }

    private string ResolveInferName(PassiveInferenceTarget kind, Guid id)
    {
        var store = _storeProvider();
        var name = kind switch
        {
            PassiveInferenceTarget.Call => OptionValue(Ds2.Core.Store.Queries.getCall(id, store))?.Name,
            _ => OptionValue(Ds2.Core.Store.Queries.getWork(id, store))?.Name,
        };
        return name ?? id.ToString("N")[..8];
    }

    private string ResolveRuntimeHubSource() => _runtimeSession?.HubSource ?? "";

    private static LogSeverity MapPassiveInferenceLogSeverity(PassiveInferenceLogKind kind) =>
        kind switch
        {
            PassiveInferenceLogKind.Warn => LogSeverity.Warn,
            _ => LogSeverity.System
        };

    private static LogSeverity MapRuntimeHubLogSeverity(RuntimeHubLogSeverity severity) =>
        severity switch
        {
            RuntimeHubLogSeverity.Warn => LogSeverity.Warn,
            RuntimeHubLogSeverity.Going => LogSeverity.Going,
            RuntimeHubLogSeverity.Finish => LogSeverity.Finish,
            RuntimeHubLogSeverity.Ready => LogSeverity.Ready,
            RuntimeHubLogSeverity.Homing => LogSeverity.Homing,
            RuntimeHubLogSeverity.System => LogSeverity.System,
            _ => LogSeverity.Info
        };

    private Status4 GetWorkStateSafe(Guid workGuid)
    {
        if (_simEngine is null) return Status4.Ready;
        var opt = _simEngine.GetWorkState(workGuid);
        return (opt != null && FSharpOption<Status4>.get_IsSome(opt)) ? opt.Value : Status4.Ready;
    }

    private bool IsMappedDeviceWork(Guid workGuid)
    {
        var engine = _simEngine;
        return engine is not null
               && (engine.IOMap.TxWorkToOutAddresses.Any(kv => kv.Key == workGuid)
                   || engine.IOMap.RxWorkToInAddresses.Any(kv => kv.Key == workGuid));
    }

    private Status4 GetCallStateSafe(Guid callGuid)
    {
        if (_simEngine is null) return Status4.Ready;
        var opt = _simEngine.GetCallState(callGuid);
        return (opt != null && FSharpOption<Status4>.get_IsSome(opt)) ? opt.Value : Status4.Ready;
    }

    private async Task SyncRuntimeBootstrapStateFromHub(
        HubConnection hub,
        RuntimeModeSession runtimeSession,
        int hubGeneration)
    {
        if (_simEngine is null) return;
        try
        {
            if (!Hub.IsCurrentConnection(hubGeneration, hub))
                return;

            var tagValues = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var address in runtimeSession.BuildHubSnapshotQueryAddresses())
            {
                if (!Hub.IsCurrentConnection(hubGeneration, hub))
                    return;
                tagValues[address] = await hub.InvokeAsync<string>(HubMethod.QueryTag, address);
            }

            // 진단용 — query 결과의 값 분포를 sim 패널 로그에 노출.
            // 모두 ""(빈 값) 이면 hub cache 가 PLC scan 전에 query 됐다는 뜻 →
            //   원인: PlcScanService initial scan 이 완료되기 전에 SyncRuntimeBootstrapStateFromHub 가 실행됨.
            // 일부 값이 "true"/"false" 면 cache 정상 → 엔진 추론 로직 자체를 보아야 함.
            var emptyCount = 0;
            var trueCount  = 0;
            var falseCount = 0;
            var sampleNonEmpty = new System.Collections.Generic.List<string>();
            foreach (var (addr, val) in tagValues)
            {
                if (string.IsNullOrEmpty(val)) emptyCount++;
                else if (val == "true") { trueCount++; if (sampleNonEmpty.Count < 5) sampleNonEmpty.Add($"{addr}=T"); }
                else { falseCount++; if (sampleNonEmpty.Count < 5) sampleNonEmpty.Add($"{addr}={val}"); }
            }
            var sampleText = sampleNonEmpty.Count > 0 ? $", 샘플=[{string.Join(",", sampleNonEmpty)}]" : "";
            _ = _dispatcher.BeginInvoke(() =>
                AddSimLog(
                    $"[Ctrl] Hub query: {tagValues.Count}개 address — 빈값={emptyCount}, true={trueCount}, false={falseCount}{sampleText}",
                    emptyCount == tagValues.Count && tagValues.Count > 0 ? LogSeverity.Warn : LogSeverity.Info));

            var effects = runtimeSession.ResolveHubSnapshotEffects(tagValues)
                .OrderBy(effect => effect.DelayMs)
                .ToArray();
            ApplyRuntimeHubEffects(effects);
        }
        catch (Exception ex)
        {
            _ = _dispatcher.BeginInvoke(() =>
                AddSimLog($"[Ctrl] Device state sync failed: {ex.Message}", LogSeverity.Warn));
        }
    }
}
