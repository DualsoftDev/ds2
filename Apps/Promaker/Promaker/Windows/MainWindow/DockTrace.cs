using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using AvalonDock.Layout;

namespace Promaker;

public partial class MainWindow
{
    private void TraceDock(string reason, LayoutAnchorable? focus = null, bool includeTree = false)
    {
        // dock layout 진단 trace — Layout.Updated 가 매 frame 단위로 발화 가능하므로 운영 환경 noise 회피 위해 Debug 레벨.
        if (!Log.IsDebugEnabled) return;

        int seq = ++_dockTraceSeq;
        Log.Debug($"[DockTrace #{seq}] {reason} focus={ContentDesc(focus)} active={ContentDesc(dockManager.Layout?.ActiveContent)} " +
                  $"anchors=[{AnchorStates()}] panes=[{PaneStates()}] floating=[{FloatingStates()}]");
        if (includeTree)
            Log.Debug($"[DockTrace #{seq} tree]{System.Environment.NewLine}{LayoutTree()}");
    }

    private string AnchorStates()
    {
        return string.Join("; ", new[] { explorerAnchor, propertyAnchor, historyAnchor, simulationAnchor, llmChatAnchor }
            .Select(a => $"{a.ContentId}:vis={a.IsVisible},hidden={a.IsHidden},float={a.IsFloating},active={a.IsActive},sel={a.IsSelected},parent={ElementDesc(a.Parent as ILayoutElement)},path={ElementPath(a)}"));
    }

    private string PaneStates()
    {
        return string.Join("; ", new[]
        {
            PaneState("explorerPane", explorerPane),
            PaneState("propertyPane", propertyPane),
            PaneState("historyPane", historyPane),
            PaneState("simulationPane", simulationPane),
            PaneState("llmChatPane", llmChatPane),
            PaneState("rightPanel", rightPanel)
        });
    }

    private string PaneState(string name, ILayoutElement element)
    {
        var container = element as ILayoutContainer;
        var children = container == null ? "" : string.Join(",", container.Children.Select(ElementDesc));
        return $"{name}:{ElementDesc(element)},root={RootDesc(element)},floating={element.FindParent<LayoutFloatingWindow>() != null}," +
               $"parent={ElementDesc(element.Parent as ILayoutElement)},w={GridLengthDesc(DockWidthOf(element))},h={GridLengthDesc(DockHeightOf(element))},children=[{children}]";
    }

    private string FloatingStates()
    {
        var floating = dockManager.Layout?.FloatingWindows;
        if (floating == null) return "null";
        return string.Join("; ", floating.Select(f => $"{ElementDesc(f)} visible={FloatingVisibleDesc(f)} children=[{ChildrenDesc(f)}]"));
    }

    private static string ChildrenDesc(ILayoutContainer container)
    {
        return string.Join(",", container.Children.Select(ElementDesc));
    }

    private static string FloatingVisibleDesc(LayoutFloatingWindow floatingWindow)
    {
        return floatingWindow switch
        {
            LayoutAnchorableFloatingWindow f => f.IsVisible.ToString(),
            LayoutDocumentFloatingWindow f => f.IsVisible.ToString(),
            _ => "n/a"
        };
    }

    private string LayoutTree()
    {
        if (dockManager.Layout == null) return "(layout null)";
        var sb = new StringBuilder();
        AppendLayoutTree(sb, dockManager.Layout, 0);
        return sb.ToString().TrimEnd();
    }

    private static void AppendLayoutTree(StringBuilder sb, ILayoutElement element, int depth)
    {
        sb.Append(' ', depth * 2);
        sb.Append(ElementDesc(element));
        sb.Append(" root=");
        sb.Append(RootDesc(element));
        sb.Append(" w=");
        sb.Append(GridLengthDesc(DockWidthOf(element)));
        sb.Append(" h=");
        sb.Append(GridLengthDesc(DockHeightOf(element)));
        if (element is ILayoutPanelElement panelElement)
        {
            sb.Append(" visible=");
            sb.Append(panelElement.IsVisible);
        }
        sb.AppendLine();

        if (element is not ILayoutContainer container) return;
        foreach (var child in container.Children.OfType<ILayoutElement>())
            AppendLayoutTree(sb, child, depth + 1);
    }

    private string ElementPath(ILayoutElement? element)
    {
        if (element == null) return "null";
        var items = new List<string>();
        for (var current = element; current != null; current = current.Parent as ILayoutElement)
            items.Add(ElementDesc(current));
        items.Reverse();
        return string.Join("/", items);
    }

    private static string ContentDesc(LayoutContent? content)
    {
        return content == null
            ? "null"
            : $"{content.ContentId ?? content.Title}:{content.GetType().Name}";
    }

    private static string ElementDesc(ILayoutElement? element)
    {
        return element switch
        {
            null => "null",
            LayoutAnchorable a => $"{a.ContentId ?? a.Title}:Anchorable",
            LayoutDocument d => $"{d.ContentId ?? d.Title}:Document",
            LayoutAnchorablePane p => $"AnchorablePane(n={p.ChildrenCount})",
            LayoutDocumentPane p => $"DocumentPane(n={p.ChildrenCount})",
            LayoutAnchorablePaneGroup g => $"AnchorablePaneGroup({g.Orientation},n={g.ChildrenCount})",
            LayoutDocumentPaneGroup g => $"DocumentPaneGroup({g.Orientation},n={g.ChildrenCount})",
            LayoutPanel p => $"LayoutPanel({p.Orientation},n={p.ChildrenCount})",
            LayoutAnchorableFloatingWindow f => $"AnchorableFloatingWindow(n={f.ChildrenCount})",
            LayoutDocumentFloatingWindow f => $"DocumentFloatingWindow(n={f.ChildrenCount})",
            LayoutRoot => "LayoutRoot",
            _ => element.GetType().Name
        };
    }

    private static string RootDesc(ILayoutElement? element)
    {
        return element?.Root switch
        {
            null => "null",
            LayoutRoot => "main",
            var root => root.GetType().Name
        };
    }

    private static GridLength? DockWidthOf(ILayoutElement? element)
    {
        return element switch
        {
            LayoutAnchorablePane p => p.DockWidth,
            LayoutDocumentPane p => p.DockWidth,
            LayoutAnchorablePaneGroup g => g.DockWidth,
            LayoutDocumentPaneGroup g => g.DockWidth,
            LayoutPanel p => p.DockWidth,
            _ => null
        };
    }

    private static GridLength? DockHeightOf(ILayoutElement? element)
    {
        return element switch
        {
            LayoutAnchorablePane p => p.DockHeight,
            LayoutDocumentPane p => p.DockHeight,
            LayoutAnchorablePaneGroup g => g.DockHeight,
            LayoutDocumentPaneGroup g => g.DockHeight,
            LayoutPanel p => p.DockHeight,
            _ => null
        };
    }

    private static string GridLengthDesc(GridLength? value)
    {
        if (value == null) return "n/a";
        var v = value.Value;
        if (v.IsAuto) return "Auto";
        if (v.IsStar) return $"{v.Value:0.###}*";
        return $"{v.Value:0.###}px";
    }
}
