using Ds2.UI.Core;

namespace Ds2.UI.Frontend.ViewModels;

internal static class TreeNodeSearch
{
    public static IEnumerable<EntityNode> EnumerateNodes(IEnumerable<EntityNode> roots)
    {
        foreach (var node in roots)
        {
            yield return node;
            foreach (var child in EnumerateNodes(node.Children))
                yield return child;
        }
    }

    public static IEnumerable<EntityNode> EnumerateVisibleNodes(IEnumerable<EntityNode> roots)
    {
        foreach (var node in roots)
        {
            yield return node;
            if (!node.IsExpanded) continue;

            foreach (var child in EnumerateVisibleNodes(node.Children))
                yield return child;
        }
    }

    public static EntityNode? FindById(IEnumerable<EntityNode> nodes, Guid id)
    {
        foreach (var node in nodes)
        {
            if (node.Id == id)
                return node;

            if (FindById(node.Children, id) is { } found)
                return found;
        }

        return null;
    }

    public static EntityNode? FindByKey(IEnumerable<EntityNode> nodes, SelectionKey key)
    {
        foreach (var node in nodes)
        {
            if (node.Id == key.Id && node.EntityType == key.EntityType)
                return node;

            if (FindByKey(node.Children, key) is { } found)
                return found;
        }

        return null;
    }

    public static EntityNode? FindParentByChildId(IEnumerable<EntityNode> nodes, Guid childId)
    {
        foreach (var node in nodes)
        {
            if (node.Children.Any(c => c.Id == childId))
                return node;

            if (FindParentByChildId(node.Children, childId) is { } found)
                return found;
        }

        return null;
    }
}
