using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace DSPilot.Components.Shared;

/// <summary>
/// SVG &lt;text&gt; 엘리먼트 컴포넌트.
/// Blazor Razor 파서가 &lt;text&gt; 태그를 예약 지시어로 인식하는 문제를
/// RenderTreeBuilder로 우회하여 근본 해결.
/// </summary>
public class SvgText : ComponentBase
{
    /// <summary>x 좌표 (int 또는 소수점 문자열 모두 허용)</summary>
    [Parameter] public object X { get; set; } = 0;

    /// <summary>y 좌표 (int 또는 소수점 문자열 모두 허용)</summary>
    [Parameter] public object Y { get; set; } = 0;

    [Parameter] public string Fill { get; set; } = "black";
    [Parameter] public int FontSize { get; set; } = 12;

    /// <summary>"700", "600" 등. null이면 속성 생략</summary>
    [Parameter] public string? FontWeight { get; set; }

    /// <summary>"middle", "end" 등. null이면 속성 생략</summary>
    [Parameter] public string? TextAnchor { get; set; }

    [Parameter] public string Content { get; set; } = "";

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "text");
        builder.AddAttribute(1, "x", X);
        builder.AddAttribute(2, "y", Y);
        builder.AddAttribute(3, "style", $"fill: {Fill}");
        builder.AddAttribute(4, "font-size", FontSize);
        if (FontWeight is not null)  builder.AddAttribute(5, "font-weight", FontWeight);
        if (TextAnchor is not null)  builder.AddAttribute(6, "text-anchor", TextAnchor);
        builder.AddContent(7, Content);
        builder.CloseElement();
    }
}
