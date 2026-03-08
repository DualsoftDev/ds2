using Ds2.UI.Core;

namespace Promaker.ViewModels;

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

    private static EntityNode? FindFirst(IEnumerable<EntityNode> nodes, Func<EntityNode, bool> predicate)
    {
        foreach (var node in nodes)
        {
            if (predicate(node))
                return node;

            if (FindFirst(node.Children, predicate) is { } found)
                return found;
        }

        return null;
    }

    public static EntityNode? FindByKey(IEnumerable<EntityNode> nodes, SelectionKey key) =>
        FindFirst(nodes, n => n.Id == key.Id && n.EntityType == key.EntityKind);
}
