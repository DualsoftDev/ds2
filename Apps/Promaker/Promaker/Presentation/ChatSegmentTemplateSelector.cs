using System.Windows;
using System.Windows.Controls;
using Promaker.ViewModels;

namespace Promaker.Presentation;

public sealed class ChatSegmentTemplateSelector : DataTemplateSelector
{
    public DataTemplate? PlainTemplate { get; set; }
    public DataTemplate? MarkdownTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
        => item switch
        {
            ChatSegment s when s.Kind == ChatSegmentKind.Markdown => MarkdownTemplate,
            ChatSegment                                            => PlainTemplate,
            _ => throw new System.InvalidOperationException(
                     $"ChatSegmentTemplateSelector 는 ChatSegment 만 지원 — 수신 = {item?.GetType().FullName ?? "null"}"),
        };
}
