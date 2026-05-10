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
        ApplyPassiveInferenceActions(actions);
        DrainPassiveInferenceLogs();
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
        ApplyPassiveInferenceActions(actions);
        DrainPassiveInferenceLogs();
    }

    private void ApplyPassiveInferenceActions(IEnumerable<PassiveInferenceAction> actions)
    {
        if (_simEngine is null)
            return;

        foreach (var action in actions)
        {
            switch (action.TargetKind)
            {
                case PassiveInferenceTarget.Work:
                    if (GetWorkStateSafe(action.TargetGuid) != action.State)
                        _simEngine.ForceWorkState(action.TargetGuid, action.State);
                    break;

                case PassiveInferenceTarget.Call:
                    if (GetCallStateSafe(action.TargetGuid) != action.State)
                        _simEngine.ForceCallState(action.TargetGuid, action.State);
                    break;
            }
        }
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
        var hubGeneration = CurrentHubGeneration;
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
        if (!ReferenceEquals(_simEngine, engine) || !IsCurrentHubGeneration(hubGeneration))
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
        var sender = _hubBatchSender;
        if (sender is null || !IsCurrentHubGeneration(hubGeneration)) return;
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
        if (!ReferenceEquals(_simEngine, engine) || !IsCurrentHubGeneration(hubGeneration))
            return;

        switch (effect.Kind)
        {
            case RuntimeHubEffectKind.Log:
                _dispatcher.BeginInvoke(() =>
                    AddSimLog(effect.Message, MapRuntimeHubLogSeverity(effect.Severity)));
                return;

            case RuntimeHubEffectKind.InjectIoByAddress:
                engine.InjectIOValueByAddress(effect.Address, effect.Value);
                return;

            case RuntimeHubEffectKind.ForceWorkState:
                if (effect.WorkGuid != Guid.Empty)
                    engine.ForceWorkState(effect.WorkGuid, effect.State);
                return;

            case RuntimeHubEffectKind.ForceWorkStateIfGoing:
                // Control 모드 IN=true 응답 전용. engine 내부 lock 안에서 atomic 으로
                // currentState=Going 일 때만 Force — Reset 흐름 도중 stale 응답이 Homing→Finish
                // 잘못 전이시키는 race 차단.
                if (effect.WorkGuid != Guid.Empty)
                    engine.TryForceWorkStateIfGoing(effect.WorkGuid, effect.State);
                return;

            case RuntimeHubEffectKind.WriteTag:
                if (_hubConnection is not null
                    && IsCurrentHubConnection(hubGeneration, _hubConnection)
                    && !string.IsNullOrEmpty(effect.Address))
                {
                    // Batch sender 가 짧은 윈도우 내 WriteTag 들을 묶어 1개 SignalR 프레임으로 송신.
                    _hubBatchSender?.Enqueue(effect.Address, effect.Value, runtimeSource);
                }
                return;

            case RuntimeHubEffectKind.PassiveObserve:
                _dispatcher.BeginInvoke(() =>
                {
                    if (ReferenceEquals(_simEngine, engine) && IsCurrentHubGeneration(hubGeneration))
                        ObserveAndInferPassiveState(effect.Address, effect.Value);
                });
                return;
        }
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
            if (!IsCurrentHubConnection(hubGeneration, hub))
                return;

            var tagValues = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var address in runtimeSession.BuildHubSnapshotQueryAddresses())
            {
                if (!IsCurrentHubConnection(hubGeneration, hub))
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
