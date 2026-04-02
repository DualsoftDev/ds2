using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Ds2.Core;
using Ds2.Store;
using Ds2.Store.DsQuery;
using Ds2.Editor;
using Promaker.Controls;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

public sealed class ExplorerPaneTests
{
    [Fact]
    public void Search_filters_tree_to_matching_branch()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.NewProjectCommand.Execute(null);

            var store = GetStore(vm);
            var projectId = Queries.allProjects(store).Head.Id;
            var systemId = Queries.activeSystemsOf(projectId, store).Head.Id;
            var flowId = Queries.flowsOf(systemId, store).Head.Id;
            var targetWorkId = store.AddWork("SearchTargetWork", flowId);
            store.AddWork("OtherWork", flowId);

            RebuildAll(vm);
            Assert.DoesNotContain(vm.ControlTreeRoots, node => node.EntityType == EntityKind.Project);

            var host = CreateHost(vm, out var pane);
            try
            {
                var searchBox = GetField<TextBox>(pane, "SearchBox");
                var controlTree = GetField<TreeView>(pane, "ControlTree");

                searchBox.Text = "SearchTargetWork";
                ApplySearchFilter(pane);

                var controlRoots = controlTree.ItemsSource!.Cast<EntityNode>().ToList();
                var systemNode = Assert.Single(controlRoots);
                var flowNode = Assert.Single(systemNode.Children);
                var workNode = Assert.Single(flowNode.Children);

                Assert.Equal(targetWorkId, workNode.Id);
                Assert.Equal("SearchTargetWork", workNode.Name);
                Assert.True(systemNode.IsExpanded);
                Assert.True(flowNode.IsExpanded);
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public void Search_matching_parent_includes_all_children_collapsed()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.NewProjectCommand.Execute(null);

            var store = GetStore(vm);
            var projectId = Queries.allProjects(store).Head.Id;
            var systemId = Queries.activeSystemsOf(projectId, store).Head.Id;
            var flowId = Queries.flowsOf(systemId, store).Head.Id;
            var work1Id = store.AddWork("Alpha", flowId);
            var work2Id = store.AddWork("Beta", flowId);

            RebuildAll(vm);

            var host = CreateHost(vm, out var pane);
            try
            {
                var searchBox = GetField<TextBox>(pane, "SearchBox");
                var controlTree = GetField<TreeView>(pane, "ControlTree");

                // Flow 이름으로 검색 → Flow가 매칭되면 하위 Work 모두 포함
                var flowName = store.FlowsReadOnly[flowId].Name;
                searchBox.Text = flowName;
                ApplySearchFilter(pane);

                var controlRoots = controlTree.ItemsSource!.Cast<EntityNode>().ToList();
                var systemNode = Assert.Single(controlRoots);
                var flowNode = Assert.Single(systemNode.Children);

                // Flow 노드는 접힌 상태 (본인이 매칭됨)
                Assert.False(flowNode.IsExpanded);
                // 하위 Work가 모두 포함됨
                Assert.Equal(2, flowNode.Children.Count);
                Assert.Contains(flowNode.Children, w => w.Id == work1Id);
                Assert.Contains(flowNode.Children, w => w.Id == work2Id);
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public void Search_matching_parent_and_child_expands_with_all_children()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.NewProjectCommand.Execute(null);

            var store = GetStore(vm);
            var projectId = Queries.allProjects(store).Head.Id;
            var systemId = Queries.activeSystemsOf(projectId, store).Head.Id;
            var flowId = Queries.flowsOf(systemId, store).Head.Id;
            var flowName = store.FlowsReadOnly[flowId].Name;

            // Flow 이름을 포함하는 Work + 포함하지 않는 Work
            var matchWorkId = store.AddWork($"{flowName}_Sub", flowId);
            var otherWorkId = store.AddWork("Unrelated", flowId);

            RebuildAll(vm);

            var host = CreateHost(vm, out var pane);
            try
            {
                var searchBox = GetField<TextBox>(pane, "SearchBox");
                var controlTree = GetField<TreeView>(pane, "ControlTree");

                searchBox.Text = flowName;
                ApplySearchFilter(pane);

                var controlRoots = controlTree.ItemsSource!.Cast<EntityNode>().ToList();
                var systemNode = Assert.Single(controlRoots);
                var flowNode = Assert.Single(systemNode.Children);

                // 부모 + 자식 모두 매칭 → 펼침
                Assert.True(flowNode.IsExpanded);
                // 매칭되지 않는 자식도 포함
                Assert.Equal(2, flowNode.Children.Count);
                Assert.Contains(flowNode.Children, w => w.Id == matchWorkId);
                Assert.Contains(flowNode.Children, w => w.Id == otherWorkId);
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public void Search_selection_opens_parent_canvas_for_work_match()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.NewProjectCommand.Execute(null);

            var store = GetStore(vm);
            var projectId = Queries.allProjects(store).Head.Id;
            var systemId = Queries.activeSystemsOf(projectId, store).Head.Id;
            var flowId = Queries.flowsOf(systemId, store).Head.Id;
            var workId = store.AddWork("CanvasSearchWork", flowId);

            RebuildAll(vm);

            var host = CreateHost(vm, out var pane);
            try
            {
                var searchBox = GetField<TextBox>(pane, "SearchBox");
                var controlTree = GetField<TreeView>(pane, "ControlTree");

                searchBox.Text = "CanvasSearchWork";
                ApplySearchFilter(pane);

                var workNode = controlTree.ItemsSource!
                    .Cast<EntityNode>()
                    .Single()
                    .Children
                    .Single()
                    .Children
                    .Single();

                InvokeHandleTreeSelectionChanged(pane, TreePaneKind.Control, workNode);

                Assert.NotNull(vm.Canvas.ActiveTab);
                Assert.Contains(vm.Canvas.CanvasNodes, node => node.Id == workId && node.EntityType == EntityKind.Work);
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public void Search_selection_opens_parent_canvas_for_device_work_match()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.NewProjectCommand.Execute(null);

            var store = GetStore(vm);
            var projectId = Queries.allProjects(store).Head.Id;
            var deviceId = store.AddSystem("DeviceSearchTarget", projectId, false);
            var flowId = store.AddFlow("DeviceFlow", deviceId);
            var workId = store.AddWork("DeviceSearchWork", flowId);

            RebuildAll(vm);

            var host = CreateHost(vm, out var pane);
            try
            {
                var searchBox = GetField<TextBox>(pane, "SearchBox");
                var deviceTree = GetField<TreeView>(pane, "DeviceTree");
                var deviceButton = GetField<ToggleButton>(pane, "DeviceTreeButton");

                deviceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DoEvents();

                searchBox.Text = "DeviceSearchWork";
                ApplySearchFilter(pane);

                var workNode = deviceTree.ItemsSource!
                    .Cast<EntityNode>()
                    .Single()
                    .Children
                    .Single()
                    .Children
                    .Single();

                InvokeHandleTreeSelectionChanged(pane, TreePaneKind.Device, workNode);

                Assert.NotNull(vm.Canvas.ActiveTab);
                Assert.Contains(vm.Canvas.CanvasNodes, node => node.Id == workId && node.EntityType == EntityKind.Work);
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public void Device_button_switches_tree_pane_visibility()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.NewProjectCommand.Execute(null);

            var host = CreateHost(vm, out var pane);
            try
            {
                var controlTree = GetField<TreeView>(pane, "ControlTree");
                var deviceTree = GetField<TreeView>(pane, "DeviceTree");
                var deviceButton = GetField<ToggleButton>(pane, "DeviceTreeButton");

                Assert.Equal(Visibility.Visible, controlTree.Visibility);
                Assert.Equal(Visibility.Collapsed, deviceTree.Visibility);

                deviceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DoEvents();

                Assert.Equal(Visibility.Collapsed, controlTree.Visibility);
                Assert.Equal(Visibility.Visible, deviceTree.Visibility);
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public void Delete_key_on_tree_deletes_selected_work_node()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.NewProjectCommand.Execute(null);

            var store = GetStore(vm);
            var projectId = Queries.allProjects(store).Head.Id;
            var systemId = Queries.activeSystemsOf(projectId, store).Head.Id;
            var flowId = Queries.flowsOf(systemId, store).Head.Id;
            var workId = store.AddWork("TreeDeleteWork", flowId);

            RebuildAll(vm);

            var workNode = FindNode(vm.ControlTreeRoots, workId);
            Assert.NotNull(workNode);

            vm.Selection.SelectNodeFromTree(workNode!, ctrlPressed: false, shiftPressed: false);
            Assert.True(vm.DeleteSelectedCommand.CanExecute(null));

            var host = CreateHost(vm, out var pane);
            try
            {
                var source = PresentationSource.FromVisual(host);
                Assert.NotNull(source);

                var args = new KeyEventArgs(Keyboard.PrimaryDevice, source!, 0, Key.Delete)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent
                };

                typeof(ExplorerPane)
                    .GetMethod("Tree_PreviewKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(pane, [pane, args]);

                DoEvents();
                Assert.False(store.WorksReadOnly.ContainsKey(workId));
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public void Rename_work_on_canvas_shows_local_name_in_tree()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.NewProjectCommand.Execute(null);

            var store = GetStore(vm);
            var projectId = Queries.allProjects(store).Head.Id;
            var systemId = Queries.activeSystemsOf(projectId, store).Head.Id;
            var flowId = Queries.flowsOf(systemId, store).Head.Id;
            var workId = store.AddWork("OriginalWork", flowId);

            RebuildAll(vm);

            var workNode = FindNode(vm.ControlTreeRoots, workId);
            Assert.NotNull(workNode);
            Assert.Equal("OriginalWork", workNode!.Name);

            // Work 이름 변경 (캔버스에서 수정하는 것과 동일 경로)
            vm.Selection.SelectNodeFromTree(workNode, ctrlPressed: false, shiftPressed: false);
            vm.RenameSelectedCommand.Execute("RenamedWork");
            DoEvents();

            // 트리에서는 LocalName만 표시되어야 함 (Flow.WorkName이 아니라)
            var treeNode = FindNode(vm.ControlTreeRoots, workId);
            Assert.NotNull(treeNode);
            Assert.Equal("RenamedWork", treeNode!.Name);

            // Store에서는 FlowPrefix.LocalName 형태로 저장
            var flowName = store.FlowsReadOnly[flowId].Name;
            Assert.Equal($"{flowName}.RenamedWork", store.WorksReadOnly[workId].Name);
        });
    }

    [Fact]
    public void Search_text_cleared_when_new_project_created()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.NewProjectCommand.Execute(null);

            var host = CreateHost(vm, out var pane);
            try
            {
                var searchBox = GetField<TextBox>(pane, "SearchBox");

                searchBox.Text = "SomeFilter";
                ApplySearchFilter(pane);
                Assert.Equal("SomeFilter", searchBox.Text);

                // 새 프로젝트 생성 → 검색 초기화
                vm.NewProjectCommand.Execute(null);
                DoEvents();

                Assert.Equal("", searchBox.Text);
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public void Call_drag_candidate_does_not_sync_selection_until_mouse_up()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.NewProjectCommand.Execute(null);

            var store = GetStore(vm);
            var projectId = Queries.allProjects(store).Head.Id;
            var systemId = Queries.activeSystemsOf(projectId, store).Head.Id;
            var flowId = Queries.flowsOf(systemId, store).Head.Id;
            var workId = store.AddWork("DragWork", flowId);
            store.AddCallsWithDevice(projectId, workId, ["Dev.Api"], true, null);
            var callId = Queries.callsOf(workId, store).Head.Id;

            RebuildAll(vm);

            var host = CreateHost(vm, out var pane);
            try
            {
                var callNode = FindNode(vm.ControlTreeRoots, callId);
                Assert.NotNull(callNode);

                var treeItem = CreateTreeItem(callNode!);
                InvokePreviewMouseLeftButtonDown(pane, treeItem);
                InvokeHandleTreeSelectionChanged(pane, TreePaneKind.Control, callNode!);

                Assert.Null(vm.SelectedNode);

                InvokeCommitPendingCallSelection(pane, callNode!, TreePaneKind.Control);

                Assert.NotNull(vm.SelectedNode);
                Assert.Equal(callId, vm.SelectedNode!.Id);
                Assert.Equal(EntityKind.Call, vm.SelectedNode.EntityType);
            }
            finally
            {
                host.Close();
            }
        });
    }

    private static Window CreateHost(MainViewModel vm, out ExplorerPane pane)
    {
        pane = new ExplorerPane { DataContext = vm };
        var host = new Window { Content = pane, Width = 400, Height = 300, ShowInTaskbar = false };
        host.Show();
        DoEvents();
        return host;
    }

    private static void RebuildAll(MainViewModel vm)
    {
        typeof(MainViewModel)
            .GetMethod("RequestRebuildAll", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, [null]);
        DoEvents();
    }

    private static void ApplySearchFilter(ExplorerPane pane)
    {
        typeof(ExplorerPane)
            .GetMethod("RefreshTreeItemsSource", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(pane, null);
        DoEvents();
    }

    private static void InvokeHandleTreeSelectionChanged(ExplorerPane pane, TreePaneKind paneKind, EntityNode node)
    {
        typeof(ExplorerPane)
            .GetMethod("HandleTreeSelectionChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(pane, [paneKind, node]);
        DoEvents();
    }

    private static void InvokePreviewMouseLeftButtonDown(ExplorerPane pane, TreeViewItem item)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent
        };

        typeof(ExplorerPane)
            .GetMethod("TreeViewItem_PreviewMouseLeftButtonDown", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(pane, [item, args]);
    }

    private static void InvokeCommitPendingCallSelection(ExplorerPane pane, EntityNode node, TreePaneKind paneKind)
    {
        typeof(ExplorerPane)
            .GetMethod("SetActiveTreePane", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(pane, [paneKind]);

        var vm = (MainViewModel)pane.DataContext!;
        vm.Selection.SetActiveTreePane(paneKind);
        vm.Selection.SelectNodeFromTree(node, ctrlPressed: false, shiftPressed: false);
        DoEvents();
    }

    private static TreeViewItem CreateTreeItem(EntityNode node) =>
        new()
        {
            DataContext = node,
            Header = node.Name
        };

    private static T GetField<T>(object owner, string name) where T : class =>
        (T)owner.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(owner)!;

    private static void DoEvents() =>
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            () => { },
            System.Windows.Threading.DispatcherPriority.Background);

    private static DsStore GetStore(MainViewModel vm)
    {
        var field = typeof(MainViewModel).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (DsStore)field.GetValue(vm)!;
    }

    private static EntityNode? FindNode(System.Collections.Generic.IEnumerable<EntityNode> roots, Guid id)
    {
        foreach (var node in roots)
        {
            if (node.Id == id)
                return node;

            var child = FindNode(node.Children, id);
            if (child is not null)
                return child;
        }

        return null;
    }
}
