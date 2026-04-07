using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Windows.Threading;
using Microsoft.FSharp.Core;
using Ds2.Core;
using Ds2.Runtime.Sim.Engine;
using Ds2.Runtime.Sim.Engine.Core;
using Ds2.Runtime.Sim.Model;
using Ds2.Core.Store;
using Ds2.Editor;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

public sealed class SimulationConnectionReloadTests
{
    [Fact]
    public void WireSimEvents_ignores_stale_status_events_after_generation_advance()
    {
        StaTestRunner.Run(() =>
        {
            var store = new DsStore();
            var projectId = store.AddProject("P");
            var systemId = store.AddSystem("S", projectId, true);
            var flowId = store.AddFlow("F", systemId);
            store.AddWork("Work1", flowId);

            var index = SimIndexModule.build(store, 10);
            using var engine = new EventDrivenEngine(index);
            var state = CreateState(() => store);

            SetPrivateField(state, "_simEngine", engine);
            state.IsSimulating = true;
            state.IsSimPaused = true;
            state.GanttChart.IsRunning = true;

            InvokeNonPublic(state, "WireSimEvents");
            InvokeNonPublic(state, "AdvanceSimUiGeneration");

            var eventField = typeof(EventDrivenEngine)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(field => field.Name.Contains("simulationStatusChangedEvent", StringComparison.Ordinal));
            var eventSource = eventField.GetValue(engine)!;
            eventSource.GetType()
                .GetMethod("Trigger")!
                .Invoke(eventSource, [new SimulationStatusChangedArgs(SimulationStatus.Running, SimulationStatus.Stopped)]);
            StaTestRunner.PumpPendingUi();

            Assert.True(state.IsSimulating);
            Assert.True(state.IsSimPaused);
            Assert.True(state.GanttChart.IsRunning);
        });
    }

    [Fact]
    public void TryWithSimEngine_swallows_engine_failure_and_reports_error()
    {
        StaTestRunner.Run(() =>
        {
            string? statusText = null;
            var store = new DsStore();
            var projectId = store.AddProject("P");
            var systemId = store.AddSystem("S", projectId, true);
            var flowId = store.AddFlow("F", systemId);
            store.AddWork("Work1", flowId);
            var index = SimIndexModule.build(store, 10);
            using var engine = new EventDrivenEngine(index);
            var state = CreateState(() => store, text => statusText = text);

            SetPrivateField(state, "_simEngine", engine);
            var method = typeof(SimulationPanelState).GetMethod(
                "TryWithSimEngine",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            var result = (bool)method.Invoke(
                state,
                ["Simulation stop", new Action<ISimulationEngine>(_ => throw new InvalidOperationException("boom"))])!;

            Assert.False(result);
            Assert.Contains("시뮬레이션 오류", statusText);
            Assert.Contains("boom", statusText);
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

    private static void SetPrivateField(object instance, string fieldName, object? value)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(instance, value);
    }

    private static void InvokeNonPublic(object instance, string methodName)
    {
        instance.GetType()
            .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(instance, null);
    }
}
