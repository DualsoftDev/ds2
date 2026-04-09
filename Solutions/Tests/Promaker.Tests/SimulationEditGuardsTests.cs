using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using Ds2.Core;
using Ds2.Core.Store;
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
    public void ApplyWorkPeriod_is_allowed_during_simulation_for_non_going_work()
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

            // 시뮬 중이지만 Going이 아니므로 Duration 변경 허용
            Assert.Equal(2500, store.GetWorkPeriodMsOrNull(workId));
            Assert.Empty(dialog.WarningMessages);
        });
    }

    [Fact]
    public void NewProject_during_simulation_shows_dialog_and_blocks_when_user_cancels()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            var dialog = new RecordingDialogService();
            SetDialogService(vm, dialog);

            vm.NewProjectCommand.Execute(null);
            var firstStore = GetStore(vm);

            vm.Simulation.IsSimulating = true;

            vm.NewProjectCommand.Execute(null);

            // 사용자가 거부 → 시뮬 유지 + 새 프로젝트 안 만들어짐 (store 동일)
            Assert.True(vm.Simulation.IsSimulating);
            Assert.Same(firstStore, GetStore(vm));
            Assert.Single(dialog.WarningMessages);
        });
    }

    [Fact]
    public void NewProject_during_simulation_proceeds_when_user_chooses_stop()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            var dialog = new StopChoosingDialogService();
            SetDialogService(vm, dialog);

            vm.NewProjectCommand.Execute(null);
            var firstStore = GetStore(vm);

            vm.Simulation.IsSimulating = true;

            vm.NewProjectCommand.Execute(null);

            // 사용자가 시뮬 종료 → 시뮬 정지 + 새 프로젝트 생성 (새 store)
            Assert.False(vm.Simulation.IsSimulating);
            Assert.NotSame(firstStore, GetStore(vm));
            Assert.Single(dialog.WarningMessages);
        });
    }

    [Fact]
    public void OpenFile_during_simulation_shows_dialog_and_blocks_when_user_cancels()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            var dialog = new RecordingDialogService();
            SetDialogService(vm, dialog);

            vm.NewProjectCommand.Execute(null);
            vm.Simulation.IsSimulating = true;

            vm.OpenFileCommand.Execute(null);

            // 거부 → 시뮬 유지 + dialog 1회 표시 (file dialog는 안 열림)
            Assert.True(vm.Simulation.IsSimulating);
            Assert.Single(dialog.WarningMessages);
        });
    }

    [Fact]
    public void Undo_during_simulation_shows_dialog_and_blocks_when_user_cancels()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            var dialog = new RecordingDialogService();
            SetDialogService(vm, dialog);

            vm.NewProjectCommand.Execute(null);
            vm.Simulation.IsSimulating = true;

            vm.UndoCommand.Execute(null);

            Assert.True(vm.Simulation.IsSimulating);
            Assert.Single(dialog.WarningMessages);
        });
    }

    [Fact]
    public void OpenIoBatchDialog_during_simulation_shows_dialog_and_blocks_when_user_cancels()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            var dialog = new RecordingDialogService();
            SetDialogService(vm, dialog);

            vm.NewProjectCommand.Execute(null);
            vm.Simulation.IsSimulating = true;

            vm.OpenIoBatchDialogCommand.Execute(null);

            Assert.True(vm.Simulation.IsSimulating);
            Assert.Single(dialog.WarningMessages);
        });
    }

    [Fact]
    public void SimStatusText_resets_to_initial_on_new_project()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            var initial = vm.Simulation.SimStatusText;

            vm.Simulation.SimStatusText = "어떤 잔존 상태 메시지";
            Assert.NotEqual(initial, vm.Simulation.SimStatusText);

            vm.NewProjectCommand.Execute(null);

            Assert.Equal(initial, vm.Simulation.SimStatusText);
        });
    }

    [Fact]
    public void SimStatusText_resets_to_initial_on_reset_for_new_store()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            var initial = vm.Simulation.SimStatusText;

            vm.Simulation.SimStatusText = "이전 프로젝트 잔존 메시지";
            Assert.NotEqual(initial, vm.Simulation.SimStatusText);

            vm.Simulation.ResetForNewStore();

            Assert.Equal(initial, vm.Simulation.SimStatusText);
        });
    }

    [Fact]
    public void AddWork_proceeds_after_user_chooses_stop_simulation_in_warning_dialog()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            var dialog = new StopChoosingDialogService();
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

            // 시뮬이 정지되고 Work가 추가되어야 함
            Assert.False(vm.Simulation.IsSimulating);
            Assert.Equal(beforeCount + 1, Queries.worksOf(flowId, store).Count());
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

    private class RecordingDialogService : IDialogService
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
        public virtual bool WarnSimulationEditBlocked(string message)
        {
            WarningMessages.Add(message);
            return false; // 기본은 stop 안 함 → 기존 테스트 동작 유지
        }
        public void ShowError(string message) { }
        public void ShowInfo(string message) { }
        public MessageBoxResult AskSaveChanges() => MessageBoxResult.No;
        public string? ShowOpenFileDialog(string filter) => null;
        public string? ShowSaveFileDialog(string filter, string? defaultFileName = null) => null;
        public T? ShowDialog<T>(Window dialog) where T : class => null;
        public bool? ShowDialog(Window dialog) => false;
    }

    private sealed class StopChoosingDialogService : RecordingDialogService
    {
        public override bool WarnSimulationEditBlocked(string message)
        {
            WarningMessages.Add(message);
            return true; // 항상 stop 선택
        }
    }
}
