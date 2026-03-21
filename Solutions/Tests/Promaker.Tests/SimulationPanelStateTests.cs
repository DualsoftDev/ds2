using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Threading;
using Ds2.Core;
using Ds2.Runtime.Sim.Model;
using Ds2.Runtime.Sim.Report;
using Ds2.Runtime.Sim.Report.Model;
using Ds2.Store;
using Ds2.Editor;
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

    private static SimulationPanelState CreateState() =>
        new(
            () => new DsStore(),
            Dispatcher.CurrentDispatcher,
            new ObservableCollection<EntityNode>(),
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
