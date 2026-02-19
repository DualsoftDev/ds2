using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Ds2.Core;
using Ds2.UI.Core;

namespace Ds2.UI.Frontend.ViewModels;

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

    /// <summary>F# ArrowVisual -> WPF geometry conversion.</summary>
    public void UpdateFromVisual(ArrowPathCalculator.ArrowVisual visual)
    {
        var points = ToPointList(visual.Points);
        PathGeometry = CreateLineGeometry(points);
        HeadGeometry = CreateHeadGeometry(ArrowType, points);

        if (points.Count == 0)
        {
            StartX = 0;
            StartY = 0;
            EndX = 0;
            EndY = 0;
            return;
        }

        var start = points[0];
        var end = points[^1];
        StartX = start.X;
        StartY = start.Y;
        EndX = end.X;
        EndY = end.Y;
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
            for (var i = 1; i < points.Count; i++)
                ctx.LineTo(points[i], true, false);
        }

        geo.Freeze();
        return geo;
    }

    private static Geometry CreateHeadGeometry(ArrowType arrowType, IReadOnlyList<Point> points)
    {
        if (points.Count < 2)
            return Geometry.Empty;

        if (arrowType is ArrowType.None or ArrowType.Group)
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
