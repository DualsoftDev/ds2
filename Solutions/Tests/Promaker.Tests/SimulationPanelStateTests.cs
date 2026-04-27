using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
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

    [Fact]
    public void Warning_marking_propagates_to_reference_work_and_call_nodes()
    {
        StaTestRunner.Run(() =>
        {
            var store = new DsStore();
            var projectId = store.AddProject("P");
            var systemId = store.AddSystem("S", projectId, true);
            var flowId = store.AddFlow("F", systemId);
            var workId = store.AddWork("W", flowId);
            var referenceWorkId = store.AddReferenceWork(workId);
            store.AddCallsWithDevice(projectId, workId, ["Dev.Api"], true, null);
            var callId = Queries.callsOf(workId, store).Head.Id;
            var referenceCallId = store.AddReferenceCall(callId);

            var canvasNodes = new ObservableCollection<EntityNode>
            {
                new(workId, EntityKind.Work, "W") { IsReference = false },
                new(referenceWorkId, EntityKind.Work, "W") { IsReference = true, ReferenceOfId = workId },
                new(callId, EntityKind.Call, "Api") { IsReference = false },
                new(referenceCallId, EntityKind.Call, "Api") { IsReference = true, ReferenceOfId = callId }
            };

            var treeNodes = new[]
            {
                new EntityNode(workId, EntityKind.Work, "W") { IsReference = false },
                new EntityNode(referenceWorkId, EntityKind.Work, "W") { IsReference = true, ReferenceOfId = workId },
                new EntityNode(callId, EntityKind.Call, "Api") { IsReference = false },
                new EntityNode(referenceCallId, EntityKind.Call, "Api") { IsReference = true, ReferenceOfId = callId }
            };

            var state = CreateState(() => store, () => canvasNodes, () => treeNodes);
            SetWarningGuids(state, workId, callId);
            InvokePrivate(state, "ApplyWarningsToCanvas");

            Assert.All(canvasNodes, node => Assert.True(node.IsWarning));
            Assert.All(treeNodes, node => Assert.True(node.IsWarning));
        });
    }

    [Fact]
    public void Warning_marking_on_device_work_propagates_to_referencing_call_nodes()
    {
        StaTestRunner.Run(() =>
        {
            var store = new DsStore();
            var projectId = store.AddProject("P");
            var systemId = store.AddSystem("S", projectId, true);
            var flowId = store.AddFlow("F", systemId);
            var workId = store.AddWork("W", flowId);
            store.AddCallsWithDevice(projectId, workId, ["KIT.External"], true, null);

            var callId = Queries.callsOf(workId, store).Head.Id;
            var referenceCallId = store.AddReferenceCall(callId);
            var apiCall = store.Calls[callId].ApiCalls.Single();
            var apiDefId = apiCall.ApiDefId?.Value ?? throw new InvalidOperationException("ApiDefId was not linked.");
            var apiDef = store.ApiDefs[apiDefId];
            var deviceWorkId = apiDef.TxGuid?.Value ?? apiDef.RxGuid?.Value ?? throw new InvalidOperationException("Device work was not linked.");

            var canvasNodes = new ObservableCollection<EntityNode>
            {
                new(callId, EntityKind.Call, "KIT.External") { IsReference = false },
                new(referenceCallId, EntityKind.Call, "KIT.External") { IsReference = true, ReferenceOfId = callId }
            };

            var treeNodes = new[]
            {
                new EntityNode(callId, EntityKind.Call, "KIT.External") { IsReference = false },
                new EntityNode(referenceCallId, EntityKind.Call, "KIT.External") { IsReference = true, ReferenceOfId = callId }
            };

            var state = CreateState(() => store, () => canvasNodes, () => treeNodes);
            SetWarningGuids(state, deviceWorkId);
            InvokePrivate(state, "ApplyWarningsToCanvas");

            Assert.All(canvasNodes, node => Assert.True(node.IsWarning));
            Assert.All(treeNodes, node => Assert.True(node.IsWarning));
        });
    }

    private static SimulationPanelState CreateState(
        Func<DsStore>? storeProvider = null,
        Func<IEnumerable<EntityNode>>? canvasNodes = null,
        Func<IEnumerable<EntityNode>>? treeNodes = null) =>
        new(
            storeProvider ?? (() => new DsStore()),
            Dispatcher.CurrentDispatcher,
            canvasNodes ?? (() => new ObservableCollection<EntityNode>()),
            treeNodes ?? (() => Array.Empty<EntityNode>()),
            _ => { });

    private static void SetWarningGuids(SimulationPanelState state, params Guid[] warningGuids)
    {
        var field = typeof(SimulationPanelState).GetField("_warningGuids", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var set = (HashSet<Guid>)field.GetValue(state)!;
        set.Clear();
        foreach (var warningGuid in warningGuids)
            set.Add(warningGuid);
    }

    private static void InvokePrivate(object target, string methodName)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(target, null);
    }
}
