using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Threading;
using Ds2.Core;
using Ds2.Runtime.Sim.Engine;
using Ds2.Runtime.Sim.Engine.Core;
using Ds2.Runtime.Sim.Model;
using Ds2.Runtime.Sim.Report;
using Ds2.Runtime.Sim.Report.Model;
using Ds2.Store;
using Ds2.Editor;
using Microsoft.FSharp.Core;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

public sealed class SimulationPanelStateTests
{
    [Fact]
    public void SimulationPanelState_defaults_speed_to_one_x()
    {
        StaTestRunner.Run(() =>
        {
            var state = CreateState();

            Assert.Equal(1.0, state.SimSpeed);
        });
    }

    [Fact]
    public void ResetSimulation_clears_runtime_state_and_restores_default_speed()
    {
        StaTestRunner.Run(() =>
        {
            var state = CreateState();
            var workId = Guid.NewGuid();

            state.HasReportData = true;
            state.IsSimulating = true;
            state.IsSimPaused = true;
            state.SimSpeed = 5.0;
            state.SimNodes.Add(new SimNodeRow
            {
                NodeGuid = workId,
                Name = "Work1",
                NodeType = "Work",
                SystemName = "SystemA",
                State = Status4.Going
            });
            state.SimEventLog.Add("log");
            state.SimWorkItems.Add(new SimWorkItem(workId, "Work1"));
            state.SelectedSimWork = state.SimWorkItems[0];
            state.GanttChart.AddEntry(workId, "Work1", EntityKind.Work);

            state.ResetSimulationCommand.Execute(null);

            Assert.False(state.HasReportData);
            Assert.False(state.IsSimulating);
            Assert.False(state.IsSimPaused);
            Assert.Equal(1.0, state.SimSpeed);
            Assert.Null(state.SelectedSimWork);
            Assert.Single(state.SimEventLog);
            Assert.Contains("F5", state.SimEventLog[0], StringComparison.Ordinal);
            Assert.Single(state.SimNodes);
            Assert.Equal(Status4.Ready, state.SimNodes[0].State);
            Assert.Empty(state.GanttChart.Entries);
        });
    }

    [Fact]
    public void ResetForNewStore_clears_simulation_collections_and_restores_default_speed()
    {
        StaTestRunner.Run(() =>
        {
            var state = CreateState();
            var workId = Guid.NewGuid();

            state.HasReportData = true;
            state.IsSimulating = true;
            state.IsSimPaused = true;
            state.SimSpeed = 10.0;
            state.SimNodes.Add(new SimNodeRow
            {
                NodeGuid = workId,
                Name = "Work1",
                NodeType = "Work",
                SystemName = "SystemA",
                State = Status4.Going
            });
            state.SimEventLog.Add("log");
            state.SimWorkItems.Add(new SimWorkItem(workId, "Work1"));
            state.SelectedSimWork = state.SimWorkItems[0];
            state.GanttChart.AddEntry(workId, "Work1", EntityKind.Work);

            state.ResetForNewStore();

            Assert.False(state.HasReportData);
            Assert.False(state.IsSimulating);
            Assert.False(state.IsSimPaused);
            Assert.Equal(1.0, state.SimSpeed);
            Assert.Null(state.SelectedSimWork);
            Assert.Empty(state.SimNodes);
            Assert.Empty(state.SimEventLog);
            Assert.Empty(state.SimWorkItems);
            Assert.Empty(state.GanttChart.Entries);
        });
    }

    [Fact]
    public void Selected_work_preference_is_instance_local()
    {
        StaTestRunner.Run(() =>
        {
            var state1 = CreateState();
            var state2 = CreateState();
            var workId = Guid.NewGuid();

            state1.SelectedSimWork = new SimWorkItem(workId, "Work1");

            var field = typeof(SimulationPanelState).GetField("_lastSelectedWorkId", BindingFlags.Instance | BindingFlags.NonPublic)!;

            Assert.Equal(workId, field.GetValue(state1));
            Assert.Null(field.GetValue(state2));
        });
    }

    [Fact]
    public void BuildReport_uses_simulation_clock_instead_of_wall_clock()
    {
        StaTestRunner.Run(() =>
        {
            var state = CreateState();
            var start = new DateTime(2026, 3, 16, 1, 2, 3, DateTimeKind.Utc);

            SetPrivateField(state, "_simStartTime", start);

            var records = (List<StateChangeRecord>)GetPrivateField(state, "_stateChangeRecords");
            records.Add(new StateChangeRecord("node-1", "Work1", "Work", "SystemA", "G", start.AddSeconds(5)));

            var buildReport = typeof(SimulationPanelState).GetMethod(
                "BuildReport",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            var report = (SimulationReport)buildReport.Invoke(state, null)!;

            Assert.Equal(start, report.Metadata.StartTime);
            Assert.Equal(start.AddSeconds(5), report.Metadata.EndTime);
            Assert.Equal(TimeSpan.FromSeconds(5), report.Metadata.TotalDuration);
        });
    }

    [Fact]
    public void SimulationStopped_event_stops_gantt_timer_and_flags()
    {
        StaTestRunner.Run(() =>
        {
            var state = CreateState();
            state.GanttChart.IsRunning = true;
            state.IsSimulating = true;
            state.IsSimPaused = true;

            var method = typeof(SimulationPanelState).GetMethod(
                "OnSimStatusChanged",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var args = new SimulationStatusChangedArgs(SimulationStatus.Running, SimulationStatus.Stopped);
            method.Invoke(state, [args]);

            Assert.False(state.GanttChart.IsRunning);
            Assert.False(state.IsSimulating);
            Assert.False(state.IsSimPaused);
            Assert.Single(state.SimEventLog);
        });
    }

    [Fact]
    public void StepSimulationCommand_is_disabled_when_terminal_work_has_no_further_progress()
    {
        StaTestRunner.Run(() =>
        {
            var store = new DsStore();
            var projectId = store.AddProject("P");
            var systemId = store.AddSystem("S", projectId, true);
            var flowId = store.AddFlow("F", systemId);
            var workId = store.AddWork("W", flowId);

            store.UpdateWorkTokenRole(workId, TokenRole.Source);
            store.UpdateWorkPeriodMs(workId, 1);

            var index = SimIndexModule.build(store, 10);
            using var engine = new EventDrivenEngine(index);
            engine.Start();
            engine.SetAllFlowStates(FlowTag.Pause);

            var token = engine.NextToken();
            engine.SeedToken(workId, token);

            var guard = 0;
            while ((engine.HasStartableWork || engine.HasActiveDuration) && guard < 8)
            {
                guard++;
                engine.Step();
            }

            var state = CreateState(() => store);
            SetPrivateField(state, "_simEngine", engine);
            SetPrivateField(state, "_isStepMode", true);
            state.IsSimulating = true;
            state.IsSimPaused = true;
            state.HasGoingCall = false;

            Assert.False(engine.HasStartableWork);
            Assert.False(engine.HasActiveDuration);
            Assert.False(state.StepSimulationCommand.CanExecute(null));
        });
    }

    [Fact]
    public void TokenEvent_notifies_step_command_when_token_shift_makes_next_work_startable()
    {
        StaTestRunner.Run(() =>
        {
            var store = new DsStore();
            var projectId = store.AddProject("P");
            var systemId = store.AddSystem("S", projectId, true);
            var flowId = store.AddFlow("F", systemId);
            var work1Id = store.AddWork("W1", flowId);
            var work2Id = store.AddWork("W2", flowId);

            store.UpdateWorkTokenRole(work1Id, TokenRole.Source);
            store.UpdateWorkPeriodMs(work1Id, 1);
            store.UpdateWorkPeriodMs(work2Id, 2000);
            store.ConnectSelectionInOrder([ work1Id, work2Id ], ArrowType.StartReset);

            var index = SimIndexModule.build(store, 10);
            using var engine = new EventDrivenEngine(index);
            engine.Start();
            engine.SetAllFlowStates(FlowTag.Pause);

            var token = engine.NextToken();
            engine.SeedToken(work1Id, token);

            engine.ForceWorkState(work1Id, Status4.Going);
            engine.ForceWorkState(work1Id, Status4.Finish);

            var shifted = false;
            for (var i = 0; i < 200 && !shifted; i++)
            {
                shifted = engine.GetWorkToken(work2Id) is not null;
                if (!shifted)
                    Thread.Sleep(10);
            }

            Assert.True(shifted);
            Assert.True(engine.HasStartableWork);

            var state = CreateState(() => store);
            SetPrivateField(state, "_simEngine", engine);
            SetPrivateField(state, "_isStepMode", true);
            state.IsSimulating = true;
            state.IsSimPaused = true;
            state.HasGoingCall = false;

            var eventRaised = false;
            state.StepSimulationCommand.CanExecuteChanged += (_, _) => eventRaised = true;

            var onTokenEvent = typeof(SimulationPanelState).GetMethod(
                "OnTokenEvent",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            onTokenEvent.Invoke(state, [new TokenEventArgs(
                TokenEventKind.Shift,
                token,
                work1Id,
                "W1",
                FSharpOption<Guid>.Some(work2Id),
                FSharpOption<string>.Some("W2"),
                engine.State.Clock)]);

            Assert.True(eventRaised);
            Assert.True(state.StepSimulationCommand.CanExecute(null));
        });
    }

    [Fact]
    public void TokenEvent_refreshes_step_mode_ui_when_only_token_shift_occurs()
    {
        StaTestRunner.Run(() =>
        {
            var store = new DsStore();
            var projectId = store.AddProject("P");
            var systemId = store.AddSystem("S", projectId, true);
            var flowId = store.AddFlow("F", systemId);
            var work1Id = store.AddWork("W1", flowId);
            var work2Id = store.AddWork("W2", flowId);

            store.UpdateWorkTokenRole(work1Id, TokenRole.Source);
            store.UpdateWorkPeriodMs(work1Id, 1);
            store.UpdateWorkPeriodMs(work2Id, 2000);
            store.ConnectSelectionInOrder([ work1Id, work2Id ], ArrowType.StartReset);

            var index = SimIndexModule.build(store, 10);
            using var engine = new EventDrivenEngine(index);
            engine.Start();
            engine.SetAllFlowStates(FlowTag.Pause);

            var token = engine.NextToken();
            engine.SeedToken(work1Id, token);

            engine.ForceWorkState(work1Id, Status4.Going);
            engine.ForceWorkState(work1Id, Status4.Finish);

            var shifted = false;
            for (var i = 0; i < 200 && !shifted; i++)
            {
                shifted = engine.GetWorkToken(work2Id) is not null;
                if (!shifted)
                    Thread.Sleep(10);
            }

            Assert.True(shifted);
            Assert.True(engine.HasStartableWork);
            Assert.False(engine.HasActiveDuration);

            var state = CreateState(() => store);
            SetPrivateField(state, "_simEngine", engine);
            SetPrivateField(state, "_isStepMode", true);
            state.IsSimulating = true;
            state.IsSimPaused = true;
            state.HasGoingCall = false;
            state.GanttChart.IsRunning = true;
            state.SimStatusText = "단계 제어 중";

            var onTokenEvent = typeof(SimulationPanelState).GetMethod(
                "OnTokenEvent",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            onTokenEvent.Invoke(state, [new TokenEventArgs(
                TokenEventKind.Shift,
                token,
                work1Id,
                "W1",
                FSharpOption<Guid>.Some(work2Id),
                FSharpOption<string>.Some("W2"),
                engine.State.Clock)]);

            Assert.False(state.GanttChart.IsRunning);
            Assert.Equal("시뮬레이션 일시정지", state.SimStatusText);
            Assert.True(state.StepSimulationCommand.CanExecute(null));
        });
    }

    [Fact]
    public void Reference_work_event_updates_shared_state_and_token_for_group()
    {
        StaTestRunner.Run(() =>
        {
            var store = new DsStore();
            var projectId = store.AddProject("P");
            var systemId = store.AddSystem("S", projectId, true);
            var flowId = store.AddFlow("F", systemId);
            var workId = store.AddWork("W1", flowId);
            var referenceWorkId = store.AddReferenceWork(workId);

            store.UpdateWorkTokenRole(workId, TokenRole.Source);

            var index = SimIndexModule.build(store, 10);
            using var engine = new EventDrivenEngine(index);
            var state = CreateState(() => store);

            SetPrivateField(state, "_simEngine", engine);
            SetPrivateField(state, "_isStepMode", true);
            state.IsSimulating = true;
            state.IsSimPaused = true;

            var initSimNodes = typeof(SimulationPanelState).GetMethod(
                "InitSimNodes",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            initSimNodes.Invoke(state, null);

            var onWorkStateChanged = typeof(SimulationPanelState).GetMethod(
                "OnWorkStateChanged",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            onWorkStateChanged.Invoke(state, [new WorkStateChangedArgs(
                workId,
                "W1",
                Status4.Ready,
                Status4.Going,
                TimeSpan.Zero)]);

            Assert.Equal(Status4.Going, state.SimNodes.Single(node => node.NodeGuid == workId).State);
            Assert.Equal(Status4.Going, state.SimNodes.Single(node => node.NodeGuid == referenceWorkId).State);

            var token = engine.NextToken();
            engine.SeedToken(workId, token);

            var onTokenEvent = typeof(SimulationPanelState).GetMethod(
                "OnTokenEvent",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            onTokenEvent.Invoke(state, [new TokenEventArgs(
                TokenEventKind.Seed,
                token,
                workId,
                "W1",
                null,
                null,
                TimeSpan.Zero)]);

            var originalDisplay = state.SimNodes.Single(node => node.NodeGuid == workId).TokenDisplay;
            var referenceDisplay = state.SimNodes.Single(node => node.NodeGuid == referenceWorkId).TokenDisplay;

            Assert.False(string.IsNullOrWhiteSpace(originalDisplay));
            Assert.EndsWith("#1", originalDisplay, StringComparison.Ordinal);
            Assert.Equal(originalDisplay, referenceDisplay);
        });
    }


    private static SimulationPanelState CreateState(Func<DsStore>? storeProvider = null) =>
        new(
            storeProvider ?? (() => new DsStore()),
            Dispatcher.CurrentDispatcher,
            () => new ObservableCollection<EntityNode>(),
            Enumerable.Empty<EntityNode>,
            _ => { });

    private static object GetPrivateField(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!;
        return field.GetValue(instance)!;
    }

    private static void SetPrivateField(object instance, string fieldName, object value)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(instance, value);
    }
}
