using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Input;
using Promaker.ViewModels;

namespace Promaker.Controls;

public partial class ExplorerPane
{
    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SearchBox.Clear();
            e.Handled = true;
            return;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!HasActiveSearch)
        {
            _searchRefreshTimer.Stop();
            RefreshTreeItemsSource();
            return;
        }

        QueueSearchRefresh();
    }

    private void SearchRefreshTimer_Tick(object? sender, EventArgs e)
    {
        _searchRefreshTimer.Stop();
        RefreshTreeItemsSource();
    }

    private void QueueSearchRefresh()
    {
        _searchRefreshTimer.Stop();
        _searchRefreshTimer.Start();
    }

    private void RefreshTreeItemsSource()
    {
        if (_boundViewModel is null)
        {
            ControlTree.ItemsSource = null;
            DeviceTree.ItemsSource = null;
            return;
        }

        if (!HasActiveSearch)
        {
            ControlTree.ItemsSource = _boundViewModel.ControlTreeRoots;
            DeviceTree.ItemsSource = _boundViewModel.DeviceTreeRoots;
            return;
        }

        var query = SearchBox.Text.Trim();
        RebuildFilteredRoots(_boundViewModel.ControlTreeRoots, FilteredControlTreeRoots, query);
        RebuildFilteredRoots(_boundViewModel.DeviceTreeRoots, FilteredDeviceTreeRoots, query);
        ControlTree.ItemsSource = FilteredControlTreeRoots;
        DeviceTree.ItemsSource = FilteredDeviceTreeRoots;
        _boundViewModel.StatusText =
            FilteredControlTreeRoots.Count + FilteredDeviceTreeRoots.Count > 0
                ? $"탐색기에서 '{query}' 검색 결과를 표시합니다."
                : $"탐색기에서 '{query}' 검색 결과가 없습니다.";
    }

    private static void BringTreeNodeIntoView(TreeView tree, Guid nodeId)
    {
        if (TryFindTreeItem(tree, nodeId, out var item) && item is not null)
            item.BringIntoView();
    }

    private static void RebuildFilteredRoots(
        IEnumerable<EntityNode> sourceRoots,
        ObservableCollection<EntityNode> targetRoots,
        string query)
    {
        targetRoots.Clear();
        foreach (var node in sourceRoots)
        {
            var filtered = CloneFilteredNode(node, query);
            if (filtered is not null)
                targetRoots.Add(filtered);
        }
    }

    private static EntityNode? CloneFilteredNode(EntityNode source, string query)
    {
        var isMatch = source.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase);

        var filteredChildren = source.Children
            .Select(child => CloneFilteredNode(child, query))
            .Where(child => child is not null)
            .Cast<EntityNode>()
            .ToList();

        if (isMatch)
        {
            var clone = CloneNode(source);

            if (filteredChildren.Count > 0)
            {
                // 부모 매칭 + 자식도 매칭 → 펼침, 모든 자식 포함 (매칭 자식은 필터 버전 사용)
                clone.IsExpanded = true;
                var filteredIds = new HashSet<Guid>(filteredChildren.Select(c => c.Id));
                foreach (var child in source.Children)
                    clone.Children.Add(filteredIds.Contains(child.Id)
                        ? filteredChildren.First(c => c.Id == child.Id)
                        : CloneSubtree(child));
            }
            else
            {
                // 부모만 매칭 → 접힘, 모든 자식 포함
                clone.IsExpanded = false;
                foreach (var child in source.Children)
                    clone.Children.Add(CloneSubtree(child));
            }

            return clone;
        }

        if (filteredChildren.Count == 0)
            return null;

        var filteredClone = CloneNode(source);
        filteredClone.IsExpanded = true;
        foreach (var child in filteredChildren)
            filteredClone.Children.Add(child);
        return filteredClone;
    }

    private static EntityNode CloneSubtree(EntityNode source)
    {
        var clone = CloneNode(source);
        foreach (var child in source.Children)
            clone.Children.Add(CloneSubtree(child));
        return clone;
    }

    private static EntityNode CloneNode(EntityNode source)
    {
        var clone = new EntityNode(source.Id, source.EntityType, source.Name, source.ParentId)
        {
            ReferenceOfId = source.ReferenceOfId
        };
        clone.X = source.X;
        clone.Y = source.Y;
        clone.Width = source.Width;
        clone.Height = source.Height;
        clone.IsSelected = source.IsSelected;
        clone.IsTreeSelected = source.IsTreeSelected;
        clone.IsExpanded = source.IsExpanded;
        clone.SelectionOrder = source.SelectionOrder;
        clone.IsGhost = source.IsGhost;
        clone.IsReference = source.IsReference;
        clone.HasAutoAux = source.HasAutoAux;
        clone.HasComAux = source.HasComAux;
        clone.HasSkipUnmatch = source.HasSkipUnmatch;
        clone.IsWarning = source.IsWarning;
        clone.IsDropTarget = source.IsDropTarget;
        clone.SimState = source.SimState;
        clone.SimTokenDisplay = source.SimTokenDisplay;
        return clone;
    }

    private static bool TryFindTreeItem(ItemsControl parent, Guid nodeId, out TreeViewItem? item)
    {
        foreach (var current in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(current) is not TreeViewItem container)
                continue;

            if (container.DataContext is EntityNode node && node.Id == nodeId)
            {
                item = container;
                return true;
            }

            container.ApplyTemplate();
            container.UpdateLayout();

            if (TryFindTreeItem(container, nodeId, out item))
                return true;
        }

        item = null;
        return false;
    }
}
