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
        foreach (var batch in RuntimeHubEffectPipeline.Build(effects))
        {
            if (batch.DelayMs <= 0)
            {
                ApplyRuntimeHubEffectBatch(
                    engine,
                    runtimeSource,
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
                    batch.Effects,
                    batch.AwaitWrites,
                    batch.RequiresExclusiveImmediateLane);
            });
        }
    }

    private void ApplyRuntimeHubEffectBatch(
        ISimulationEngine engine,
        string runtimeSource,
        IReadOnlyList<RuntimeHubEffect> effects,
        bool awaitWrites,
        bool requiresExclusiveImmediateLane)
    {
        if (!requiresExclusiveImmediateLane)
        {
            foreach (var effect in effects)
                ApplyRuntimeHubEffect(engine, runtimeSource, effect, awaitWrites);
            return;
        }

        lock (_runtimeImmediateEffectLock)
        {
            foreach (var effect in effects)
                ApplyRuntimeHubEffect(engine, runtimeSource, effect, awaitWrites);
        }
    }

    private void ApplyRuntimeHubEffect(
        ISimulationEngine engine,
        string runtimeSource,
        RuntimeHubEffect effect,
        bool awaitWrite)
    {
        switch (effect.Kind)
        {
            case RuntimeHubEffectKind.Log:
                _dispatcher.BeginInvoke(() =>
                    AddSimLog(effect.Message, MapRuntimeHubLogSeverity(effect.Severity)));
                return;

            case RuntimeHubEffectKind.InjectIoByAddress:
                if (ReferenceEquals(_simEngine, engine))
                    engine.InjectIOValueByAddress(effect.Address, effect.Value);
                return;

            case RuntimeHubEffectKind.ForceWorkState:
                if (ReferenceEquals(_simEngine, engine) && effect.WorkGuid != Guid.Empty)
                    engine.ForceWorkState(effect.WorkGuid, effect.State);
                return;

            case RuntimeHubEffectKind.WriteTag:
                if (_hubConnection is { State: HubConnectionState.Connected } hub
                    && !string.IsNullOrEmpty(effect.Address))
                {
                    var writeTask = InvokeRuntimeHubWriteTagAsync(hub, effect.Address, effect.Value, runtimeSource);
                    if (awaitWrite)
                        writeTask.GetAwaiter().GetResult();
                }
                return;

            case RuntimeHubEffectKind.PassiveObserve:
                if (ReferenceEquals(_simEngine, engine))
                    _dispatcher.BeginInvoke(() => ObserveAndInferPassiveState(effect.Address, effect.Value));
                return;
        }
    }

    private async Task InvokeRuntimeHubWriteTagAsync(
        HubConnection hub,
        string address,
        string value,
        string runtimeSource)
    {
        try
        {
            await hub.InvokeAsync(HubMethod.WriteTag, address, value, runtimeSource);
        }
        catch (Exception ex)
        {
            _dispatcher.BeginInvoke(() =>
                AddSimLog($"[Hub] WriteTag failed: {ex.Message}", LogSeverity.Error));
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
        RuntimeModeSession runtimeSession)
    {
        if (_simEngine is null) return;
        try
        {
            var tagValues = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var address in runtimeSession.BuildHubSnapshotQueryAddresses())
            {
                tagValues[address] = await hub.InvokeAsync<string>(HubMethod.QueryTag, address);
            }

            var effects = runtimeSession.ResolveHubSnapshotEffects(tagValues)
                .OrderBy(effect => effect.DelayMs)
                .ToArray();
            ApplyRuntimeHubEffectBatch(
                _simEngine,
                ResolveRuntimeHubSource(),
                effects,
                awaitWrites: true,
                requiresExclusiveImmediateLane: false);
        }
        catch (Exception ex)
        {
            _dispatcher.BeginInvoke(() =>
                AddSimLog($"[Ctrl] Device state sync failed: {ex.Message}", LogSeverity.Warn));
        }
    }
}
