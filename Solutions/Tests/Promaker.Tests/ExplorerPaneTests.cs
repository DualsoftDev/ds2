using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Promaker.Controls;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

public sealed class ExplorerPaneTests
{
    [Fact]
    public void Search_filters_to_matching_descendant_and_keeps_ancestor_path()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.NewProjectCommand.Execute(null);

            var store = GetStore(vm);
            var projectId = Queries.allProjects(store).Head.Id;
            var systemId = Queries.activeSystemsOf(projectId, store).Head.Id;
            var flowId = Queries.flowsOf(systemId, store).Head.Id;
            store.AddWork("SearchTargetWork", flowId);
            store.AddWork("OtherWork", flowId);

            Assert.True(StaTestRunner.WaitUntil(1000, () => Flatten(vm.ControlTreeRoots).Any(node => node.Name == "SearchTargetWork")));

            var host = CreateHost(vm, out var pane);
            try
            {
                var searchBox = GetNamed<TextBox>(pane, "SearchBox");
                var controlTree = GetNamed<TreeView>(pane, "ControlTree");

                searchBox.Text = "SearchTargetWork";

                Assert.True(StaTestRunner.WaitUntil(1000, () => ReferenceEquals(controlTree.ItemsSource, pane.FilteredControlTreeRoots)));
                Assert.True(StaTestRunner.WaitUntil(1000, () => pane.FilteredControlTreeRoots.Count == 1));

                var systemNode = Assert.Single(pane.FilteredControlTreeRoots);
                var flowNode = Assert.Single(systemNode.Children);
                var workNode = Assert.Single(flowNode.Children);

                Assert.Equal(EntityKind.System, systemNode.EntityType);
                Assert.Equal(EntityKind.Flow, flowNode.EntityType);
                Assert.Equal(EntityKind.Work, workNode.EntityType);
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
    public void Selecting_filtered_control_work_opens_parent_canvas()
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

            Assert.True(StaTestRunner.WaitUntil(1000, () => Flatten(vm.ControlTreeRoots).Any(node => node.Id == workId)));

            var host = CreateHost(vm, out var pane);
            try
            {
                var searchBox = GetNamed<TextBox>(pane, "SearchBox");
                var controlTree = GetNamed<TreeView>(pane, "ControlTree");

                searchBox.Text = "CanvasSearchWork";

                Assert.True(StaTestRunner.WaitUntil(1000, () => pane.FilteredControlTreeRoots.Count == 1));
                ExpandAllContainers(controlTree);

                var item = FindTreeViewItem(controlTree, workId);
                Assert.NotNull(item);

                item!.IsSelected = true;
                StaTestRunner.PumpPendingUi();

                Assert.True(StaTestRunner.WaitUntil(
                    1000,
                    () => vm.Canvas.ActiveTab is not null
                        && vm.Canvas.CanvasNodes.Any(node => node.Id == workId && node.EntityType == EntityKind.Work)));
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public void Device_toggle_and_filtered_device_selection_open_parent_canvas()
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

            Assert.True(StaTestRunner.WaitUntil(1000, () => Flatten(vm.DeviceTreeRoots).Any(node => node.Id == workId)));

            var host = CreateHost(vm, out var pane);
            try
            {
                var searchBox = GetNamed<TextBox>(pane, "SearchBox");
                var controlTree = GetNamed<TreeView>(pane, "ControlTree");
                var deviceTree = GetNamed<TreeView>(pane, "DeviceTree");
                var controlButton = GetNamed<ToggleButton>(pane, "ControlTreeButton");
                var deviceButton = GetNamed<ToggleButton>(pane, "DeviceTreeButton");

                deviceButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                StaTestRunner.PumpPendingUi();

                Assert.False(controlButton.IsChecked ?? true);
                Assert.True(deviceButton.IsChecked ?? false);
                Assert.Equal(Visibility.Collapsed, controlTree.Visibility);
                Assert.Equal(Visibility.Visible, deviceTree.Visibility);

                searchBox.Text = "DeviceSearchWork";

                Assert.True(StaTestRunner.WaitUntil(1000, () => pane.FilteredDeviceTreeRoots.Count == 1));
                ExpandAllContainers(deviceTree);

                var item = FindTreeViewItem(deviceTree, workId);
                Assert.NotNull(item);

                item!.IsSelected = true;
                StaTestRunner.PumpPendingUi();

                Assert.True(StaTestRunner.WaitUntil(
                    1000,
                    () => vm.Canvas.ActiveTab is not null
                        && vm.Canvas.CanvasNodes.Any(node => node.Id == workId && node.EntityType == EntityKind.Work)));
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public void Clicking_already_selected_tree_item_resyncs_property_panel()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.NewProjectCommand.Execute(null);

            var store = GetStore(vm);
            var projectId = Queries.allProjects(store).Head.Id;
            var systemId = Queries.activeSystemsOf(projectId, store).Head.Id;
            var flowId = Queries.flowsOf(systemId, store).Head.Id;
            var workId = store.AddWork("ReselectWork", flowId);

            Assert.True(StaTestRunner.WaitUntil(1000, () => Flatten(vm.ControlTreeRoots).Any(node => node.Id == workId)));

            var host = CreateHost(vm, out var pane);
            try
            {
                var controlTree = GetNamed<TreeView>(pane, "ControlTree");
                ExpandAllContainers(controlTree);

                var item = FindTreeViewItem(controlTree, workId);
                Assert.NotNull(item);

                item!.IsSelected = true;
                StaTestRunner.PumpPendingUi();

                Assert.Equal(workId, vm.PropertyPanel.SelectedNode?.Id);

                vm.PropertyPanel.SyncSelection(null, []);
                Assert.Null(vm.PropertyPanel.SelectedNode);

                var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent
                };

                item.RaiseEvent(args);
                StaTestRunner.PumpPendingUi();

                Assert.Equal(workId, vm.PropertyPanel.SelectedNode?.Id);
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public void Clicking_wpf_selected_tree_item_resyncs_property_panel_even_when_selection_state_was_cleared()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.NewProjectCommand.Execute(null);

            var store = GetStore(vm);
            var projectId = Queries.allProjects(store).Head.Id;
            var systemId = Queries.activeSystemsOf(projectId, store).Head.Id;
            var flowId = Queries.flowsOf(systemId, store).Head.Id;
            var workId = store.AddWork("WpfReselectWork", flowId);

            Assert.True(StaTestRunner.WaitUntil(1000, () => Flatten(vm.ControlTreeRoots).Any(node => node.Id == workId)));

            var host = CreateHost(vm, out var pane);
            try
            {
                var controlTree = GetNamed<TreeView>(pane, "ControlTree");
                ExpandAllContainers(controlTree);

                var item = FindTreeViewItem(controlTree, workId);
                Assert.NotNull(item);

                item!.IsSelected = true;
                StaTestRunner.PumpPendingUi();

                Assert.Equal(workId, vm.PropertyPanel.SelectedNode?.Id);

                vm.Selection.ClearNodeSelection();
                vm.PropertyPanel.SyncSelection(null, []);
                StaTestRunner.PumpPendingUi();

                Assert.Null(vm.PropertyPanel.SelectedNode);
                Assert.False(Flatten(vm.ControlTreeRoots).First(node => node.Id == workId).IsTreeSelected);
                Assert.True(item.IsSelected);

                var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent
                };

                item.RaiseEvent(args);
                StaTestRunner.PumpPendingUi();

                Assert.Equal(workId, vm.PropertyPanel.SelectedNode?.Id);
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public void Null_selection_change_from_hidden_device_tree_does_not_switch_active_pane()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.NewProjectCommand.Execute(null);

            var store = GetStore(vm);
            var projectId = Queries.allProjects(store).Head.Id;
            var systemId = store.AddSystem("DeviceSystem", projectId, false);
            var flowId = store.AddFlow("DeviceFlow", systemId);
            store.AddWork("DeviceWork", flowId);

            var host = CreateHost(vm, out var pane);
            try
            {
                var controlButton = GetNamed<ToggleButton>(pane, "ControlTreeButton");
                var deviceButton = GetNamed<ToggleButton>(pane, "DeviceTreeButton");
                var method = typeof(ExplorerPane).GetMethod("HandleTreeSelectionChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;

                Assert.True(controlButton.IsChecked ?? false);
                Assert.False(deviceButton.IsChecked ?? true);

                method.Invoke(pane, [TreePaneKind.Device, null]);
                StaTestRunner.PumpPendingUi();

                Assert.True(controlButton.IsChecked ?? false);
                Assert.False(deviceButton.IsChecked ?? true);
                Assert.Equal(TreePaneKind.Control, vm.Selection.ActiveTreePane);
            }
            finally
            {
                host.Close();
            }
        });
    }

    private static DsStore GetStore(MainViewModel vm)
    {
        var field = typeof(MainViewModel).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (DsStore)field.GetValue(vm)!;
    }

    private static T GetNamed<T>(FrameworkElement root, string name)
        where T : FrameworkElement =>
        (T)root.FindName(name)!;

    private static Window CreateHost(MainViewModel vm, out ExplorerPane pane)
    {
        pane = new ExplorerPane
        {
            DataContext = vm
        };

        var host = new Window
        {
            Width = 420,
            Height = 640,
            Content = pane,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None
        };

        host.Show();
        pane.Measure(new Size(420, 640));
        pane.Arrange(new Rect(0, 0, 420, 640));
        pane.UpdateLayout();
        StaTestRunner.PumpPendingUi();
        return host;
    }

    private static IEnumerable<EntityNode> Flatten(IEnumerable<EntityNode> roots)
    {
        foreach (var node in roots)
        {
            yield return node;
            foreach (var child in Flatten(node.Children))
                yield return child;
        }
    }

    private static void ExpandAllContainers(ItemsControl parent)
    {
        parent.ApplyTemplate();
        parent.UpdateLayout();

        foreach (var item in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem container)
                continue;

            container.IsExpanded = true;
            container.ApplyTemplate();
            container.UpdateLayout();
            ExpandAllContainers(container);
        }
    }

    private static TreeViewItem? FindTreeViewItem(ItemsControl parent, Guid nodeId)
    {
        foreach (var item in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem container)
                continue;

            if (container.DataContext is EntityNode node && node.Id == nodeId)
                return container;

            container.IsExpanded = true;
            container.ApplyTemplate();
            container.UpdateLayout();

            var nested = FindTreeViewItem(container, nodeId);
            if (nested is not null)
                return nested;
        }

        return null;
    }
}
