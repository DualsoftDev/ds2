using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

public sealed class SimulationPanelStateTests
{
    [Fact]
    public void Pause_command_is_available_immediately_when_simulating()
    {
        StaTestRunner.Run(() =>
        {
            var state = CreateState();

            state.IsSimulating = true;
            Assert.False(state.IsSimPaused);
            Assert.True(state.PauseSimulationCommand.CanExecute(null));

            state.IsSimPaused = true;
            Assert.True(state.IsSimPaused);
            Assert.False(state.PauseSimulationCommand.CanExecute(null));
        });
    }

    [Fact]
    public void ResetForNewStore_clears_public_runtime_collections_and_defaults()
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
            state.SimEventLog.Add(new SimLogEntry("log"));
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
    public void SyncCanvasSelection_selects_matching_work_only_while_simulating()
    {
        StaTestRunner.Run(() =>
        {
            var state = CreateState();
            var work1Id = Guid.NewGuid();
            var work2Id = Guid.NewGuid();

            state.SimWorkItems.Add(new SimWorkItem(work1Id, "Work1"));
            state.SimWorkItems.Add(new SimWorkItem(work2Id, "Work2"));

            state.SyncCanvasSelection([new SelectionKey(work2Id, EntityKind.Work)]);
            Assert.Null(state.SelectedSimWork);

            state.IsSimulating = true;
            state.SyncCanvasSelection([new SelectionKey(work2Id, EntityKind.Work)]);

            Assert.NotNull(state.SelectedSimWork);
            Assert.Equal(work2Id, state.SelectedSimWork!.Guid);
        });
    }

    [Fact]
    public void CanChangeSpeed_depends_on_running_and_paused_state()
    {
        StaTestRunner.Run(() =>
        {
            var state = CreateState();

            Assert.True(state.CanChangeSpeed);

            state.IsSimulating = true;
            state.IsSimPaused = false;
            Assert.False(state.CanChangeSpeed);

            state.IsSimPaused = true;
            Assert.True(state.CanChangeSpeed);
        });
    }

    private static SimulationPanelState CreateState(Func<DsStore>? storeProvider = null) =>
        new(
            storeProvider ?? (() => new DsStore()),
            Dispatcher.CurrentDispatcher,
            () => new ObservableCollection<EntityNode>(),
            () => Array.Empty<EntityNode>(),
            _ => { });
}
