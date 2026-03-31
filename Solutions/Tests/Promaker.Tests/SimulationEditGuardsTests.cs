using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using Ds2.Core;
using Ds2.Store;
using Ds2.Store.DsQuery;
using Ds2.Editor;
using Promaker.Services;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

public sealed class SimulationEditGuardsTests
{
    [Fact]
    public void AddWork_is_blocked_during_simulation_before_prompt()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            var dialog = new RecordingDialogService();
            SetDialogService(vm, dialog);

            vm.NewProjectCommand.Execute(null);

            var store = GetStore(vm);
            var projectId = Queries.allProjects(store).Head.Id;
            var systemId = Queries.activeSystemsOf(projectId, store).Head.Id;
            var flowId = Queries.flowsOf(systemId, store).Head.Id;

            vm.Canvas.OpenTabs.Add(new CanvasTab(systemId, TabKind.System, "System"));
            vm.Canvas.ActiveTab = vm.Canvas.OpenTabs[0];
            vm.Simulation.IsSimulating = true;

            var beforeCount = Queries.worksOf(flowId, store).Count();

            vm.AddWorkCommand.Execute(null);

            Assert.Equal(beforeCount, Queries.worksOf(flowId, store).Count());
            Assert.Single(dialog.WarningMessages);
            Assert.Equal(0, dialog.PromptNameCount);
        });
    }

    [Fact]
    public void DeleteSelected_blocks_node_deletion_but_allows_arrow_deletion_during_simulation()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            var dialog = new RecordingDialogService();
            SetDialogService(vm, dialog);

            vm.NewProjectCommand.Execute(null);

            var store = GetStore(vm);
            var projectId = Queries.allProjects(store).Head.Id;
            var systemId = Queries.activeSystemsOf(projectId, store).Head.Id;
            var flowId = Queries.flowsOf(systemId, store).Head.Id;
            var work1Id = store.AddWork("Work1", flowId);
            var work2Id = store.AddWork("Work2", flowId);
            store.ConnectSelectionInOrder([work1Id, work2Id], ArrowType.StartReset);
            var arrowId = store.ArrowWorksReadOnly.Values.Single().Id;

            var work1Node = new EntityNode(work1Id, EntityKind.Work, "NewFlow.Work1");
            var work2Node = new EntityNode(work2Id, EntityKind.Work, "NewFlow.Work2");
            var arrowNode = new ArrowNode(arrowId, work1Id, work2Id, ArrowType.StartReset);

            vm.Canvas.CanvasNodes.Add(work1Node);
            vm.Canvas.CanvasNodes.Add(work2Node);
            vm.Canvas.CanvasArrows.Add(arrowNode);
            vm.Simulation.IsSimulating = true;

            vm.Selection.SelectNodeFromCanvas(work1Node, ctrlPressed: false, shiftPressed: false);
            vm.DeleteSelectedCommand.Execute(null);

            Assert.True(store.WorksReadOnly.ContainsKey(work1Id));
            Assert.Single(dialog.WarningMessages);

            vm.Selection.ClearNodeSelection();
            vm.Selection.SelectArrowFromCanvas(arrowNode, ctrlPressed: false);
            vm.DeleteSelectedCommand.Execute(null);

            Assert.False(store.ArrowWorksReadOnly.ContainsKey(arrowId));
            Assert.Single(dialog.WarningMessages);
        });
    }

    [Fact]
    public void ApplyWorkPeriod_is_blocked_during_simulation()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            var dialog = new RecordingDialogService();
            SetDialogService(vm, dialog);

            vm.NewProjectCommand.Execute(null);

            var store = GetStore(vm);
            var projectId = Queries.allProjects(store).Head.Id;
            var systemId = Queries.activeSystemsOf(projectId, store).Head.Id;
            var flowId = Queries.flowsOf(systemId, store).Head.Id;
            var workId = store.AddWork("Work1", flowId);
            store.UpdateWorkPeriodMs(workId, 1000);

            var workNode = new EntityNode(workId, EntityKind.Work, "NewFlow.Work1");
            vm.Canvas.CanvasNodes.Add(workNode);
            vm.Selection.SelectNodeFromCanvas(workNode, ctrlPressed: false, shiftPressed: false);
            vm.Simulation.IsSimulating = true;

            vm.PropertyPanel.WorkPeriodMs = 2500;
            vm.PropertyPanel.ApplyWorkPeriodCommand.Execute(null);

            Assert.Equal(1000, store.GetWorkPeriodMsOrNull(workId));
            Assert.Single(dialog.WarningMessages);
        });
    }

    private static void SetDialogService(MainViewModel vm, IDialogService dialogService)
    {
        typeof(MainViewModel)
            .GetField("_dialogService", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm, dialogService);
    }

    private static DsStore GetStore(MainViewModel vm)
    {
        var field = typeof(MainViewModel).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (DsStore)field.GetValue(vm)!;
    }

    private sealed class RecordingDialogService : IDialogService
    {
        public int PromptNameCount { get; private set; }
        public List<string> WarningMessages { get; } = [];

        public string? PromptName(string title, string defaultName)
        {
            PromptNameCount++;
            return defaultName;
        }

        public bool Confirm(string message, string title) => true;
        public void ShowWarning(string message) => WarningMessages.Add(message);
        public void ShowError(string message) { }
        public void ShowInfo(string message) { }
        public MessageBoxResult AskSaveChanges() => MessageBoxResult.No;
        public string? ShowOpenFileDialog(string filter) => null;
        public string? ShowSaveFileDialog(string filter, string? defaultFileName = null) => null;
        public T? ShowDialog<T>(Window dialog) where T : class => null;
        public bool? ShowDialog(Window dialog) => false;
    }
}
