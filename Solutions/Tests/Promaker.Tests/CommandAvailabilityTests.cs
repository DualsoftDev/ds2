using System;
using System.Collections.Generic;
using System.Reflection;
using Ds2.Core;
using Ds2.Store;
using Ds2.Editor;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

public sealed class CommandAvailabilityTests
{
    [Fact]
    public void Selection_and_clipboard_commands_follow_current_context()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();

            Assert.False(vm.CopySelectedCommand.CanExecute(null));
            Assert.False(vm.PasteCopiedCommand.CanExecute(null));
            Assert.False(vm.FocusNameEditorCommand.CanExecute(null));
            Assert.False(vm.ImportMermaidCommand.CanExecute(null));
            Assert.False(vm.Canvas.FocusSelectedInCanvasCommand.CanExecute(null));

            var work = new EntityNode(Guid.NewGuid(), EntityKind.Work, "Work1");
            vm.Canvas.CanvasNodes.Add(work);
            vm.SelectedNode = work;

            Assert.True(vm.CopySelectedCommand.CanExecute(null));
            Assert.True(vm.FocusNameEditorCommand.CanExecute(null));
            Assert.True(vm.ImportMermaidCommand.CanExecute(null));
            Assert.True(vm.Canvas.FocusSelectedInCanvasCommand.CanExecute(null));

            var clipboard = GetClipboard(vm);
            clipboard.Add(new SelectionKey(Guid.NewGuid(), EntityKind.Work));
            vm.Canvas.OpenTabs.Add(new CanvasTab(Guid.NewGuid(), TabKind.Flow, "Flow"));
            vm.Canvas.ActiveTab = vm.Canvas.OpenTabs[0];

            Assert.True(vm.PasteCopiedCommand.CanExecute(null));

            vm.SelectedNode = new EntityNode(Guid.NewGuid(), EntityKind.Call, "Call1");

            Assert.False(vm.ImportMermaidCommand.CanExecute(null));
            Assert.True(vm.Canvas.FocusSelectedInCanvasCommand.CanExecute(null));

            vm.SelectedNode = new EntityNode(Guid.NewGuid(), EntityKind.System, "System1");

            Assert.True(vm.ImportMermaidCommand.CanExecute(null));
            Assert.False(vm.Canvas.FocusSelectedInCanvasCommand.CanExecute(null));
        });
    }

    [Fact]
    public void Add_and_layout_commands_follow_tab_context()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();

            Assert.False(vm.AddWorkCommand.CanExecute(null));
            Assert.False(vm.AddCallCommand.CanExecute(null));
            Assert.False(vm.AutoLayoutCommand.CanExecute(null));

            vm.NewProjectCommand.Execute(null);

            var store = GetStore(vm);
            var projectId = DsQuery.allProjects(store).Head.Id;
            var systemId = DsQuery.activeSystemsOf(projectId, store).Head.Id;
            var flowId = DsQuery.flowsOf(systemId, store).Head.Id;

            vm.Canvas.OpenTabs.Add(new CanvasTab(systemId, TabKind.System, "System"));
            vm.Canvas.ActiveTab = vm.Canvas.OpenTabs[0];

            Assert.True(vm.AddWorkCommand.CanExecute(null));
            Assert.False(vm.AddCallCommand.CanExecute(null));
            Assert.True(vm.AutoLayoutCommand.CanExecute(null));

            var workId = store.AddWork("Work1", flowId);
            vm.Canvas.OpenTabs.Add(new CanvasTab(workId, TabKind.Work, "Work1"));
            vm.Canvas.ActiveTab = vm.Canvas.OpenTabs[1];

            Assert.True(vm.AddCallCommand.CanExecute(null));
        });
    }

    [Fact]
    public void Delete_command_requires_non_project_selection()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.NewProjectCommand.Execute(null);

            var store = GetStore(vm);
            var projectId = DsQuery.allProjects(store).Head.Id;

            Assert.False(vm.DeleteSelectedCommand.CanExecute(null));

            vm.SelectedNode = new EntityNode(projectId, EntityKind.Project, "Project");
            Assert.False(vm.DeleteSelectedCommand.CanExecute(null));

            vm.SelectedNode = new EntityNode(Guid.NewGuid(), EntityKind.Work, "Work1");
            Assert.True(vm.DeleteSelectedCommand.CanExecute(null));
        });
    }

    [Fact]
    public void ApplyName_command_requires_non_empty_editor_text()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.SelectedNode = new EntityNode(Guid.NewGuid(), EntityKind.Work, "Flow1.Work1");

            vm.PropertyPanel.NameEditorText = "   ";
            Assert.False(vm.PropertyPanel.ApplyNameCommand.CanExecute(null));

            vm.PropertyPanel.NameEditorText = "Renamed";
            Assert.True(vm.PropertyPanel.ApplyNameCommand.CanExecute(null));
        });
    }

    private static DsStore GetStore(MainViewModel vm)
    {
        var field = typeof(MainViewModel).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (DsStore)field.GetValue(vm)!;
    }

    private static List<SelectionKey> GetClipboard(MainViewModel vm)
    {
        var field = typeof(MainViewModel).GetField("_clipboardSelection", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (List<SelectionKey>)field.GetValue(vm)!;
    }
}
