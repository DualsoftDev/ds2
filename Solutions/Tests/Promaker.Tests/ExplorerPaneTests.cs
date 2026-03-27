using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Ds2.Core;
using Ds2.Store;
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
            var projectId = DsQuery.allProjects(store).Head.Id;
            var systemId = DsQuery.activeSystemsOf(projectId, store).Head.Id;
            var flowId = DsQuery.flowsOf(systemId, store).Head.Id;
            var targetWorkId = store.AddWork("SearchTargetWork", flowId);
            store.AddWork("OtherWork", flowId);

            typeof(MainViewModel)
                .GetMethod("RequestRebuildAll", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(vm, [null]);
            DoEvents();

            Assert.DoesNotContain(vm.ControlTreeRoots, node => node.EntityType == EntityKind.Project);

            var pane = new ExplorerPane { DataContext = vm };
            var host = new Window { Content = pane, Width = 400, Height = 300, ShowInTaskbar = false };
            host.Show();
            try
            {
                DoEvents();

                var searchBox = (TextBox)typeof(ExplorerPane)
                    .GetField("SearchBox", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(pane)!;
                var controlTree = (TreeView)typeof(ExplorerPane)
                    .GetField("ControlTree", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(pane)!;

                searchBox.Text = "SearchTargetWork";
                DoEvents();

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
    public void Device_button_switches_tree_pane_visibility()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.NewProjectCommand.Execute(null);

            var pane = new ExplorerPane { DataContext = vm };
            var host = new Window { Content = pane, Width = 400, Height = 300, ShowInTaskbar = false };
            host.Show();
            try
            {
                DoEvents();

                var controlTree = (TreeView)typeof(ExplorerPane)
                    .GetField("ControlTree", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(pane)!;
                var deviceTree = (TreeView)typeof(ExplorerPane)
                    .GetField("DeviceTree", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(pane)!;
                var deviceButton = (System.Windows.Controls.Primitives.ToggleButton)typeof(ExplorerPane)
                    .GetField("DeviceTreeButton", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(pane)!;

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
            var projectId = DsQuery.allProjects(store).Head.Id;
            var systemId = DsQuery.activeSystemsOf(projectId, store).Head.Id;
            var flowId = DsQuery.flowsOf(systemId, store).Head.Id;
            var workId = store.AddWork("TreeDeleteWork", flowId);

            typeof(MainViewModel)
                .GetMethod("RequestRebuildAll", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(vm, [null]);
            DoEvents();

            var workNode = FindNode(vm.ControlTreeRoots, workId);
            Assert.NotNull(workNode);

            vm.Selection.SelectNodeFromTree(workNode!, ctrlPressed: false, shiftPressed: false);
            Assert.True(vm.DeleteSelectedCommand.CanExecute(null));

            var pane = new ExplorerPane { DataContext = vm };
            var host = new Window { Content = pane, Width = 400, Height = 300, ShowInTaskbar = false };
            host.Show();
            try
            {
                DoEvents();

                var source = PresentationSource.FromVisual(host);
                Assert.NotNull(source);

                var args = new KeyEventArgs(Keyboard.PrimaryDevice, source!, 0, Key.Delete)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent
                };

                var method = typeof(ExplorerPane).GetMethod("Tree_PreviewKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!;
                method.Invoke(pane, [pane, args]);

                DoEvents();
                Assert.False(store.WorksReadOnly.ContainsKey(workId));
            }
            finally
            {
                host.Close();
            }
        });
    }

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
