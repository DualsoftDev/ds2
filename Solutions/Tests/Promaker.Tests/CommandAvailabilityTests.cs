using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Controls;
using System.Windows;
using Ds2.Core;
using Ds2.Store;
using Ds2.Store.DsQuery;
using Ds2.Editor;
using Promaker.Controls;
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
            var projectId = Queries.allProjects(store).Head.Id;
            var systemId = Queries.activeSystemsOf(projectId, store).Head.Id;
            var flowId = Queries.flowsOf(systemId, store).Head.Id;

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
    public void Add_and_connect_commands_follow_selected_node_context()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.NewProjectCommand.Execute(null);

            var store = GetStore(vm);
            var projectId = Queries.allProjects(store).Head.Id;
            var systemId = Queries.activeSystemsOf(projectId, store).Head.Id;
            var flowId = Queries.flowsOf(systemId, store).Head.Id;
            var work1Id = store.AddWork("Work1", flowId);
            var work2Id = store.AddWork("Work2", flowId);
            vm.Canvas.OpenTabs.Add(new CanvasTab(systemId, TabKind.System, "System"));
            vm.Canvas.ActiveTab = vm.Canvas.OpenTabs[0];

            var work1Node = new EntityNode(work1Id, EntityKind.Work, "Flow.Work1");
            var work2Node = new EntityNode(work2Id, EntityKind.Work, "Flow.Work2");
            var callNode = new EntityNode(Guid.NewGuid(), EntityKind.Call, "Call1");
            vm.Canvas.CanvasNodes.Add(work1Node);
            vm.Canvas.CanvasNodes.Add(work2Node);
            vm.Canvas.CanvasNodes.Add(callNode);

            vm.SelectedNode = work1Node;
            Assert.False(vm.AddWorkCommand.CanExecute(null));

            vm.SelectedNode = callNode;
            Assert.False(vm.AddCallCommand.CanExecute(null));

            vm.Selection.SelectNodeFromCanvas(work1Node, ctrlPressed: false, shiftPressed: false);
            Assert.False(vm.ConnectSelectedNodesCommand.CanExecute(null));

            vm.Selection.SelectNodeFromCanvas(work2Node, ctrlPressed: true, shiftPressed: false);
            Assert.True(vm.ConnectSelectedNodesCommand.CanExecute(null));
            Assert.False(vm.FocusNameEditorCommand.CanExecute(null));
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
            var projectId = Queries.allProjects(store).Head.Id;

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

    [Fact]
    public void Focus_name_editor_highlight_stays_visible_until_selection_changes()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            var panel = new PropertyPanel
            {
                DataContext = vm.PropertyPanel
            };

            panel.Measure(new System.Windows.Size(400, 600));
            panel.Arrange(new System.Windows.Rect(0, 0, 400, 600));
            panel.UpdateLayout();

            vm.SelectedNode = new EntityNode(Guid.NewGuid(), EntityKind.Work, "Flow1.Work1");
            vm.FocusNameEditorRequested = panel.FocusNameEditorControl;

            vm.FocusNameEditorCommand.Execute(null);

            var nameEditor = (TextBox)panel.FindName("NameEditor")!;
            var highlight = (Border)panel.FindName("NameEditorHighlight")!;
            Assert.True(vm.PropertyPanel.IsNameEditHighlighted);
            Assert.Equal("Work1", vm.PropertyPanel.NameEditorText);
            Assert.Equal(nameEditor.Text.Length, nameEditor.SelectionLength);
            Assert.Equal(1d, highlight.Opacity);
            Assert.Equal(0, Grid.GetColumn(highlight));
            Assert.Equal(1, Grid.GetColumnSpan(highlight));

            vm.SelectedNode = new EntityNode(Guid.NewGuid(), EntityKind.Work, "Flow1.Work2");

            Assert.False(vm.PropertyPanel.IsNameEditHighlighted);
        });
    }

    [Fact]
    public void Cancel_name_edit_restores_current_name_and_clears_highlight()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.SelectedNode = new EntityNode(Guid.NewGuid(), EntityKind.Work, "Flow1.Work1");

            vm.PropertyPanel.BeginNameEditGuidance();
            vm.PropertyPanel.NameEditorText = "Renamed";

            vm.PropertyPanel.CancelNameEdit();

            Assert.False(vm.PropertyPanel.IsNameEditHighlighted);
            Assert.False(vm.PropertyPanel.IsNameDirty);
            Assert.Equal("Flow1.", vm.PropertyPanel.NamePrefix);
            Assert.Equal("Work1", vm.PropertyPanel.NameEditorText);
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
