using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Threading;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Ds2.Runtime.Engine;
using Ds2.Runtime.Engine.Core;
using Ds2.Runtime.IO;
using Ds2.Runtime.Model;
using Microsoft.FSharp.Control;
using Microsoft.FSharp.Core;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

public sealed class SimulationPanelStateRuntimeTests
{
    [Fact]
    public void Continuous_injection_restarts_an_armed_source_when_it_returns_to_ready()
    {
        StaTestRunner.Run(() =>
        {
            var store = BuildSourceStore(out var sourceWorkId);
            var index = SimIndexModule.build(store, 10);
            var engine = new FakeSimulationEngine(index);
            var state = CreateState(() => store);

            SetSimEngine(state, engine);
            state.IsSimulating = true;
            state.IsContinuousInjectionEnabled = true;
            state.SelectedSimWork = new SimWorkItem(sourceWorkId, "Source");

            Assert.True(state.ForceWorkStartCommand.CanExecute(null));
            state.ForceWorkStartCommand.Execute(null);

            Assert.Equal([sourceWorkId], engine.StartedSourceGuids);
            Assert.Equal([sourceWorkId], engine.SeededSourceGuids);
            Assert.Equal([(sourceWorkId, Status4.Going)], engine.ForcedWorkTransitions);

            engine.ClearWorkToken(sourceWorkId);
            engine.SetWorkState(sourceWorkId, Status4.Ready);
            InvokePrivate(
                state,
                "OnWorkStateChanged",
                new WorkStateChangedArgs(
                    sourceWorkId,
                    "Source",
                    Status4.Homing,
                    Status4.Ready,
                    TimeSpan.Zero));

            Assert.Equal([sourceWorkId, sourceWorkId], engine.StartedSourceGuids);
            Assert.Equal([sourceWorkId, sourceWorkId], engine.SeededSourceGuids);
            Assert.Equal(
                [(sourceWorkId, Status4.Going), (sourceWorkId, Status4.Going)],
                engine.ForcedWorkTransitions);
        });
    }

    [Fact]
    public void Continuous_injection_does_not_restart_source_when_toggle_is_off()
    {
        StaTestRunner.Run(() =>
        {
            var store = BuildSourceStore(out var sourceWorkId);
            var index = SimIndexModule.build(store, 10);
            var engine = new FakeSimulationEngine(index);
            var state = CreateState(() => store);

            SetSimEngine(state, engine);
            state.IsSimulating = true;
            state.SelectedSimWork = new SimWorkItem(sourceWorkId, "Source");

            Assert.True(state.ForceWorkStartCommand.CanExecute(null));
            state.ForceWorkStartCommand.Execute(null);

            engine.ClearWorkToken(sourceWorkId);
            engine.SetWorkState(sourceWorkId, Status4.Ready);
            InvokePrivate(
                state,
                "OnWorkStateChanged",
                new WorkStateChangedArgs(
                    sourceWorkId,
                    "Source",
                    Status4.Homing,
                    Status4.Ready,
                    TimeSpan.Zero));

            Assert.Equal([sourceWorkId], engine.StartedSourceGuids);
            Assert.Equal([sourceWorkId], engine.SeededSourceGuids);
            Assert.Equal([(sourceWorkId, Status4.Going)], engine.ForcedWorkTransitions);
        });
    }

    [Fact]
    public void Step_uses_engine_step_boundary_without_resuming_runtime_thread()
    {
        StaTestRunner.Run(() =>
        {
            var store = BuildSourceStore(out _);
            var index = SimIndexModule.build(store, 10);
            var engine = new FakeSimulationEngine(index);
            var state = CreateState(() => store);

            SetSimEngine(state, engine);
            state.IsSimulating = true;
            state.IsSimPaused = true;
            state.SelectedSimWork = SimWorkItem.AutoStart;

            Assert.True(state.StepSimulationCommand.CanExecute(null));
            state.StepSimulationCommand.Execute(null);

            Assert.Equal([(Guid.Empty, true)], engine.StepWithSourcePrimingCalls);
            Assert.Equal(0, engine.ResumeCalls);
            Assert.Equal(0, engine.PauseCalls);
            Assert.Empty(engine.StartedSourceGuids);
            Assert.False(state.GanttChart.IsRunning);
        });
    }

    private static SimulationPanelState CreateState(
        Func<DsStore>? storeProvider = null,
        Action<string>? setStatusText = null) =>
        new(
            storeProvider ?? (() => new DsStore()),
            Dispatcher.CurrentDispatcher,
            () => new ObservableCollection<EntityNode>(),
            () => Array.Empty<EntityNode>(),
            setStatusText ?? (_ => { }));

    private static void InvokePrivate(object target, string methodName, object argument)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(target, [argument]);
    }

    private static void SetSimEngine(SimulationPanelState state, ISimulationEngine engine)
    {
        typeof(SimulationPanelState)
            .GetField("_simEngine", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(state, engine);
    }

    private static DsStore BuildSourceStore(out Guid sourceWorkId)
    {
        var store = new DsStore();
        var projectId = store.AddProject("P");
        var systemId = store.AddSystem("S", projectId, true);
        var flowId = store.AddFlow("F", systemId);
        sourceWorkId = store.AddWork("Source", flowId);
        store.UpdateWorkTokenRole(sourceWorkId, TokenRole.Source);
        return store;
    }

    private static DsStore BuildHomingFixture()
    {
        var store = new DsStore();
        var projectId = store.AddProject("P");
        var activeSystemId = store.AddSystem("Active", projectId, true);
        var activeFlowId = store.AddFlow("Flow", activeSystemId);
        var activeWorkId = store.AddWork("Main", activeFlowId);

        var deviceSystemId = store.AddSystem("Device", projectId, false);
        var deviceFlowId = store.AddFlow("DeviceFlow", deviceSystemId);
        var advanceWorkId = store.AddWork("ADV", deviceFlowId);
        var returnWorkId = store.AddWork("RET", deviceFlowId);
        store.UpdateWorkPeriodMs(advanceWorkId, 50);
        store.UpdateWorkPeriodMs(returnWorkId, 50);

        var finishedProps = new SimulationWorkProperties
        {
            IsFinished = true
        };
        store.Works[returnWorkId].SetSimulationProperties(finishedProps);
        store.ConnectSelectionInOrder([advanceWorkId, returnWorkId], ArrowType.ResetReset);

        var advanceApiDefId = AddDeviceApiDef(store, deviceSystemId, "ADV", advanceWorkId);
        var returnApiDefId = AddDeviceApiDef(store, deviceSystemId, "RET", returnWorkId);

        var returnCallId = store.AddCallWithLinkedApiDefs(activeWorkId, "Device", "RET", [returnApiDefId]);
        var advanceCallId = store.AddCallWithLinkedApiDefs(activeWorkId, "Device", "ADV", [advanceApiDefId]);
        store.ConnectSelectionInOrder([returnCallId, advanceCallId], ArrowType.Start);

        return store;
    }

    private static Guid AddDeviceApiDef(DsStore store, Guid systemId, string name, Guid deviceWorkId)
    {
        var apiDefId = store.AddApiDefWithProperties(name, systemId);
        store.UpdateApiDef(
            apiDefId,
            name,
            ApiDefActionType.Normal,
            FSharpOption<Guid>.Some(deviceWorkId),
            FSharpOption<Guid>.Some(deviceWorkId));
        return apiDefId;
    }

    #pragma warning disable CS0067
    private sealed class FakeSimulationEngine(SimIndex index) : ISimulationEngine
    {
        private SimState _state = SimStateModule.create(10, index.AllWorkGuids, index.AllCallGuids, index.AllFlowGuids);
        private int _nextTokenId;

        public List<Guid> SeededSourceGuids { get; } = [];
        public List<Guid> StartedSourceGuids { get; } = [];
        public List<(Guid WorkGuid, Status4 State)> ForcedWorkTransitions { get; } = [];
        public List<(Guid SelectedSourceGuid, bool AutoStartSources)> StepWithSourcePrimingCalls { get; } = [];
        public int PauseCalls { get; private set; }
        public int ResumeCalls { get; private set; }
        public bool CanAdvanceStepResult { get; set; } = true;
        public bool StepWithSourcePrimingResult { get; set; } = true;

        public event FSharpHandler<WorkStateChangedArgs>? WorkStateChanged;
        public event FSharpHandler<CallStateChangedArgs>? CallStateChanged;
        public event FSharpHandler<SimulationStatusChangedArgs>? SimulationStatusChanged;
        public event FSharpHandler<TokenEventArgs>? TokenEvent;
        public event FSharpHandler<CallTimeoutArgs>? CallTimeout;
        public event FSharpHandler<EventArgs>? HomingPhaseCompleted;

        public SimState State => _state;
        public SimulationStatus Status => SimulationStatus.Running;
        public SimIndex Index => index;
        public bool HasStartableWork => false;
        public bool HasActiveDuration => false;
        public long CurrentTimeMs => 0;
        public FSharpOption<long> NextEventTimeMs => null!;
        public double SpeedMultiplier { get; set; } = 1.0;
        public bool TimeIgnore { get; set; }
        public SignalIOMap IOMap => throw new NotSupportedException();
        public bool IsHomingPhase => false;

        public void Start() => throw new NotSupportedException();
        public void Pause() => PauseCalls++;
        public void Resume() => ResumeCalls++;
        public void Stop() { }
        public void Reset() => throw new NotSupportedException();
        public void ApplyInitialStates() => throw new NotSupportedException();
        public void ForceCallState(Guid callGuid, Status4 newState) => throw new NotSupportedException();
        public void TryForceWorkStateIfGoing(Guid workGuid, Status4 newState) => throw new NotSupportedException();
        public FSharpOption<Status4> GetCallState(Guid callGuid) => null!;
        public void SetAllFlowStates(FlowTag tag) => throw new NotSupportedException();
        public FlowTag GetFlowState(Guid flowGuid) => FlowTag.Ready;
        public bool Step() => throw new NotSupportedException();
        public bool CanAdvanceStep(Guid selectedSourceGuid, bool autoStartSources) => CanAdvanceStepResult;
        public bool StepWithSourcePriming(Guid selectedSourceGuid, bool autoStartSources)
        {
            StepWithSourcePrimingCalls.Add((selectedSourceGuid, autoStartSources));
            return StepWithSourcePrimingResult;
        }
        public void ReloadConnections() => throw new NotSupportedException();
        public void ReloadDurations() => throw new NotSupportedException();
        public void InjectIOValue(Guid apiCallGuid, string value) => throw new NotSupportedException();
        public bool InjectIOValueByAddress(string address, string value) => throw new NotSupportedException();
        public void StartSourceWork(Guid sourceWorkGuid)
        {
            StartedSourceGuids.Add(sourceWorkGuid);
            if (GetWorkToken(sourceWorkGuid) is null)
                SeedToken(sourceWorkGuid, NextToken());
            ForceWorkState(sourceWorkGuid, Status4.Going);
        }
        public void DiscardToken(Guid workGuid) => _state = SimStateModule.setWorkToken(workGuid, null, _state);
        public FSharpOption<TokenValue> GetWorkToken(Guid workGuid) =>
            SimStateModule.getWorkToken(workGuid, _state);
        public FSharpOption<Tuple<string, int>> GetTokenOrigin(TokenValue token) => null!;
        public bool StartWithHomingPhase() => throw new NotSupportedException();
        public void Dispose() { }

        public void ForceWorkState(Guid workGuid, Status4 newState)
        {
            ForcedWorkTransitions.Add((workGuid, newState));
            SetWorkState(workGuid, newState);
        }

        public FSharpOption<Status4> GetWorkState(Guid workGuid) =>
            _state.WorkStates.TryFind(workGuid);

        public TokenValue NextToken() => TokenValue.NewIntToken(++_nextTokenId);

        public void SeedToken(Guid sourceWorkGuid, TokenValue value)
        {
            SeededSourceGuids.Add(sourceWorkGuid);
            _state = SimStateModule.setWorkToken(sourceWorkGuid, FSharpOption<TokenValue>.Some(value), _state);
        }

        public void ClearWorkToken(Guid workGuid) =>
            _state = SimStateModule.setWorkToken(workGuid, null, _state);

        public void SetWorkState(Guid workGuid, Status4 state) =>
            _state = SimStateModule.setWorkState(workGuid, state, _state);
    }
    #pragma warning restore CS0067
}
