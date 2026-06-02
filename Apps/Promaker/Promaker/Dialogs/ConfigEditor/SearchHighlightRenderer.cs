using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace Promaker.Dialogs.ConfigEditor;

/// <summary>JSON 에디터의 검색 매치 배경 하이라이트 (VSCode 식 — 전체 매치 + 현재 매치 강조).
/// AvalonEdit SearchPanel 이 find 전용이라, 자체 find/replace 오버레이가 이 렌더러로 매치를 그린다.</summary>
internal sealed class SearchHighlightRenderer : IBackgroundRenderer
{
    private Brush _matchBrush = Frozen(Color.FromArgb(80, 255, 200, 0));
    private Brush _currentBrush = Frozen(Color.FromArgb(150, 255, 170, 0));

    public List<ISegment> Matches { get; } = new();
    public ISegment? Current { get; set; }

    public KnownLayer Layer => KnownLayer.Selection;

    /// <summary>테마(라이트/다크)에 맞춰 하이라이트 색 교체.</summary>
    public void SetColors(Color match, Color current)
    {
        _matchBrush = Frozen(match);
        _currentBrush = Frozen(current);
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (Matches.Count == 0) return;
        textView.EnsureVisualLines();
        foreach (var seg in Matches)
        {
            var isCurrent = Current is { } c && c.Offset == seg.Offset && c.Length == seg.Length;
            var builder = new BackgroundGeometryBuilder { CornerRadius = 2, AlignToWholePixels = true };
            builder.AddSegment(textView, seg);
            var geometry = builder.CreateGeometry();
            if (geometry is not null)
                drawingContext.DrawGeometry(isCurrent ? _currentBrush : _matchBrush, null, geometry);
        }
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
