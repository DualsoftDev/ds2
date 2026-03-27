using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Threading;
using Ds2.Core;
using Ds2.Runtime.Sim.Engine;
using Ds2.Runtime.Sim.Engine.Core;
using Ds2.Store;
using Ds2.Editor;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

public sealed class SimulationConnectionReloadTests
{
    [Fact]
    public void NotifyConnectionsChanged_preserves_running_progress_when_group_arrows_are_deleted()
    {
        StaTestRunner.Run(() =>
        {
            var store = new DsStore();
            var projectId = store.AddProject("P");
            var systemId = store.AddSystem("S", projectId, true);
            var flowId = store.AddFlow("F", systemId);
            var work1Id = store.AddWork("Work1", flowId);
            var work2Id = store.AddWork("Work2", flowId);
            var work21Id = store.AddWork("Work2_1", flowId);
            var work22Id = store.AddWork("Work2_2", flowId);

            store.UpdateWorkTokenRole(work1Id, TokenRole.Source);
            store.UpdateWorkPeriodMs(work1Id, 500);
            store.UpdateWorkPeriodMs(work2Id, 1);
            store.UpdateWorkPeriodMs(work21Id, 1);
            store.UpdateWorkPeriodMs(work22Id, 1);
            store.AddCallsWithDevice(projectId, work1Id, ["Dev.Api1", "Dev.Api2", "Dev.Api3"], true, null);

            store.ConnectSelectionInOrder([work1Id, work2Id], ArrowType.StartReset);
            store.ConnectSelectionInOrder([work2Id, work21Id, work22Id], ArrowType.Group);

            var index = SimIndexModule.build(store, 10);
            using var engine = new EventDrivenEngine(index);
            var state = CreateState(() => store);

            SetPrivateField(state, "_simEngine", engine);
            state.IsSimulating = true;
            state.GanttChart.Reset(DateTime.Now);
            InvokeNonPublic(state, "InitSimNodes");
            InvokeNonPublic(state, "InitGanttEntries");

            engine.Start();

            var token = engine.NextToken();
            engine.SeedToken(work1Id, token);

            var work1CallIds = DsQuery.callsOf(work1Id, store).Select(call => call.Id).ToList();
            Assert.True(
                WaitUntil(1500, () =>
                    engine.GetWorkState(work1Id).Value == Status4.Going),
                "predecessor work should be running before the group links are removed");

            var groupArrowIds = DsQuery.arrowWorksOf(systemId, store)
                .Where(arrow => arrow.ArrowType == ArrowType.Group)
                .Select(arrow => arrow.Id)
                .ToList();
            store.RemoveArrows(groupArrowIds);

            state.NotifyConnectionsChanged();

            Assert.True(
                WaitUntil(4000, () => engine.GetWorkToken(work2Id) is not null),
                "central successor should still receive token after predecessor progression continues past the reload");
            Assert.True(
                WaitUntil(1500, () =>
                    engine.GetWorkState(work2Id).Value is Status4.Going or Status4.Finish),
                "central successor should keep progressing after reload");
            Assert.True(
                WaitUntil(1500, () => engine.GetWorkState(work1Id).Value != Status4.Going),
                "predecessor should not remain stuck in Going after the reload");

            Assert.Null(engine.GetWorkToken(work21Id));
            Assert.Null(engine.GetWorkToken(work22Id));
            Assert.Equal(Status4.Ready, engine.GetWorkState(work21Id).Value);
            Assert.Equal(Status4.Ready, engine.GetWorkState(work22Id).Value);
        });
    }

    private static SimulationPanelState CreateState(Func<DsStore>? storeProvider = null) =>
        new(
            storeProvider ?? (() => new DsStore()),
            Dispatcher.CurrentDispatcher,
            () => new ObservableCollection<EntityNode>(),
            Enumerable.Empty<EntityNode>,
            _ => { });

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

    private static bool WaitUntil(int timeoutMs, Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var matched = predicate();
        while (!matched && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
            matched = predicate();
        }

        return matched;
    }
}
