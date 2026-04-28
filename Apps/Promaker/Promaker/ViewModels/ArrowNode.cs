using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;

namespace Promaker.ViewModels;

/// <summary>Canvas arrow view model.</summary>
public partial class ArrowNode : ObservableObject
{
    private const double MarkerSize = 15.0;
    private const double MinSegmentLength = 0.001;

    public ArrowNode(Guid id, Guid sourceId, Guid targetId, ArrowType arrowType)
    {
        Id = id;
        SourceId = sourceId;
        TargetId = targetId;
        ArrowType = arrowType;
    }

    public Guid Id { get; }
    public Guid SourceId { get; }
    public Guid TargetId { get; }
    public ArrowType ArrowType { get; }

    [ObservableProperty] private Geometry? _pathGeometry;
    [ObservableProperty] private Geometry? _headGeometry;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private double _startX;
    [ObservableProperty] private double _startY;
    [ObservableProperty] private double _endX;
    [ObservableProperty] private double _endY;

    private List<Point>? _lastPoints;
    private List<Point>? _dragSnapshot;

    /// <summary>F# ArrowVisual -> WPF geometry conversion.</summary>
    public void UpdateFromVisual(ArrowPathCalculator.ArrowVisual visual)
    {
        SetGeometryFromPoints(ToPointList(visual.Points));
    }

    /// <summary>드래그 시작 시 현재 경로를 스냅샷한다. 드래그 중에는 이 스냅샷을 평행이동해서 그린다.</summary>
    public void BeginDragSnapshot()
    {
        _dragSnapshot = _lastPoints is null ? null : new List<Point>(_lastPoints);
    }

    public void EndDragSnapshot()
    {
        _dragSnapshot = null;
    }

    /// <summary>스냅샷한 경로의 source-측 점들은 srcDelta로, target-측 점들은 tgtDelta로 평행이동.</summary>
    public void ApplyDragTranslation(Vector srcDelta, Vector tgtDelta)
    {
        if (_dragSnapshot is null || _dragSnapshot.Count == 0)
            return;

        var pts = _dragSnapshot;
        // 4-point cubic Bezier [start, cp1, cp2, end]: [0,1] = source side, [2,3] = target side
        // 그 외 폴리라인은 중앙에서 분할하여 동일하게 처리
        var srcSideCount = pts.Count / 2;

        var translated = new List<Point>(pts.Count);
        for (var i = 0; i < pts.Count; i++)
        {
            var d = i < srcSideCount ? srcDelta : tgtDelta;
            translated.Add(new Point(pts[i].X + d.X, pts[i].Y + d.Y));
        }

        SetGeometryFromPoints(translated);
    }

    private void SetGeometryFromPoints(List<Point> points)
    {
        PathGeometry = CreateLineGeometry(points);
        HeadGeometry = CreateHeadGeometry(ArrowType, points);

        if (points.Count == 0)
        {
            StartX = 0;
            StartY = 0;
            EndX = 0;
            EndY = 0;
            _lastPoints = null;
            return;
        }

        var start = points[0];
        var end = points[^1];
        StartX = start.X;
        StartY = start.Y;
        EndX = end.X;
        EndY = end.Y;
        _lastPoints = points;
    }

    private static List<Point> ToPointList(
        Microsoft.FSharp.Collections.FSharpList<Tuple<double, double>> points)
    {
        var result = new List<Point>();
        foreach (var point in points)
            result.Add(new Point(point.Item1, point.Item2));

        return result;
    }

    private static Geometry CreateLineGeometry(IReadOnlyList<Point> points)
    {
        if (points.Count == 0)
            return Geometry.Empty;

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            if (points.Count == 4)
            {
                // Cubic Bezier: [start, cp1, cp2, end]
                ctx.BezierTo(points[1], points[2], points[3], true, false);
            }
            else
            {
                for (var i = 1; i < points.Count; i++)
                    ctx.LineTo(points[i], true, false);
            }
        }

        geo.Freeze();
        return geo;
    }

    private static Geometry CreateHeadGeometry(ArrowType arrowType, IReadOnlyList<Point> points)
    {
        if (points.Count < 2)
            return Geometry.Empty;

        if (arrowType is ArrowType.Unspecified or ArrowType.Group)
            return Geometry.Empty;

        if (!TryGetDirection(points, fromEnd: true, out var endDirection))
            return Geometry.Empty;

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            var start = points[0];
            var end = points[^1];

            // End marker: closed kite shape.
            AppendKiteHead(ctx, end, endDirection, MarkerSize);

            if (arrowType == ArrowType.StartReset && TryGetDirection(points, fromEnd: false, out var startForwardDirection))
            {
                // StartReset: add a small square marker at source side.
                AppendStartSquare(ctx, start, startForwardDirection, MarkerSize * 0.5);
            }
            else if (arrowType == ArrowType.ResetReset && TryGetDirection(points, fromEnd: false, out var startForwardDirectionForReset))
            {
                // ResetReset: add backward kite marker at source side.
                var startBackwardDirection = new Vector(-startForwardDirectionForReset.X, -startForwardDirectionForReset.Y);
                AppendKiteHead(ctx, start, startBackwardDirection, MarkerSize);
            }
        }

        geo.Freeze();
        return geo;
    }

    private static bool TryGetDirection(IReadOnlyList<Point> points, bool fromEnd, out Vector direction)
    {
        if (fromEnd)
        {
            for (var i = points.Count - 1; i > 0; i--)
            {
                var segment = points[i] - points[i - 1];
                if (segment.Length <= MinSegmentLength)
                    continue;

                segment.Normalize();
                direction = segment;
                return true;
            }
        }
        else
        {
            for (var i = 1; i < points.Count; i++)
            {
                var segment = points[i] - points[i - 1];
                if (segment.Length <= MinSegmentLength)
                    continue;

                segment.Normalize();
                direction = segment;
                return true;
            }
        }

        direction = default;
        return false;
    }

    private static void AppendKiteHead(StreamGeometryContext ctx, Point tip, Vector direction, double markerSize)
    {
        var perpendicular = new Vector(-direction.Y, direction.X);
        var wing1 = tip - direction * markerSize * 0.55 + perpendicular * markerSize * 0.4;
        var wing2 = tip - direction * markerSize * 0.55 - perpendicular * markerSize * 0.4;

        ctx.BeginFigure(wing1, false, false);
        ctx.LineTo(tip, true, false);
        ctx.LineTo(wing2, true, false);
    }

    private static void AppendStartSquare(StreamGeometryContext ctx, Point start, Vector forwardDirection, double size)
    {
        var perpendicular = new Vector(-forwardDirection.Y, forwardDirection.X);
        var center = start + forwardDirection * size * 0.5;
        var c1 = center + forwardDirection * size * 0.5 + perpendicular * size * 0.5;
        var c2 = center + forwardDirection * size * 0.5 - perpendicular * size * 0.5;
        var c3 = center - forwardDirection * size * 0.5 - perpendicular * size * 0.5;
        var c4 = center - forwardDirection * size * 0.5 + perpendicular * size * 0.5;

        ctx.BeginFigure(c1, true, true);
        ctx.LineTo(c2, true, false);
        ctx.LineTo(c3, true, false);
        ctx.LineTo(c4, true, false);
    }
}
