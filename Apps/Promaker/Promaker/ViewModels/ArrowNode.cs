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

    /// <summary>양방향 ResetReset 쌍의 파트너 화살표 Id. null이면 단방향.</summary>
    [ObservableProperty] private Guid? _bidirectionalPartnerId;
    /// <summary>양방향 쌍 중 중앙 마커를 그리는 쪽 (중복 방지). 데이터 모델은 그대로 두 화살표지만 시각/선택만 통합.</summary>
    [ObservableProperty] private bool _renderCenterMarker;
    [ObservableProperty] private double _centerX;
    [ObservableProperty] private double _centerY;
    /// <summary>StartReset 양방향일 때 가운데 원에 붙는 네모 꼬리(원래 시작 사각형 보존). 그 외 타입에선 null.</summary>
    [ObservableProperty] private Geometry? _centerTailGeometry;

    private List<Point>? _lastPoints;
    private List<Point>? _dragSnapshot;

    public bool IsBidirectional => BidirectionalPartnerId.HasValue;
    /// <summary>화살표 끝 핸들(시작/끝 ellipse) 가시성. 선택 + 비양방향일 때만.</summary>
    public bool EndpointHandlesVisible => IsSelected && !IsBidirectional;

    /// <summary>드래그/동기화용으로 보존된 풀 경로 점들 (양방향이어도 절반이 아닌 전체).</summary>
    public IReadOnlyList<Point>? LastPoints => _lastPoints;

    partial void OnBidirectionalPartnerIdChanged(Guid? value)
    {
        OnPropertyChanged(nameof(IsBidirectional));
        OnPropertyChanged(nameof(EndpointHandlesVisible));
        if (_lastPoints is { Count: > 0 })
            SetGeometryFromPoints(_lastPoints);
    }

    partial void OnIsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(EndpointHandlesVisible));
    }

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

    /// <summary>외부에서 경로점을 직접 지정 (양방향 쌍 동기화 등에 사용).</summary>
    public void SetPathPoints(IReadOnlyList<Point> points)
    {
        SetGeometryFromPoints(new List<Point>(points));
    }

    private void SetGeometryFromPoints(List<Point> points)
    {
        if (points.Count == 0)
        {
            PathGeometry = Geometry.Empty;
            HeadGeometry = Geometry.Empty;
            StartX = 0;
            StartY = 0;
            EndX = 0;
            EndY = 0;
            CenterX = 0;
            CenterY = 0;
            _lastPoints = null;
            return;
        }

        var renderPoints = points;
        var head = ArrowType;
        var midpoint = points[^1];

        if (IsBidirectional)
        {
            var (_, second, mid) = SplitPathAtMidpoint(points);
            renderPoints = second;
            midpoint = mid;
            // 양방향에서는 source-측 마커(ResetReset의 reverse kite, StartReset의 square)는 모두 억제.
            // 파트너 화살표가 자기 쪽 끝에 head를 그림. 결과적으로 양 끝에 kite head만 있는 깔끔한 라인.
            if (head == ArrowType.ResetReset || head == ArrowType.StartReset)
                head = ArrowType.Reset;
        }

        PathGeometry = CreateLineGeometry(renderPoints);
        HeadGeometry = CreateHeadGeometry(head, renderPoints);
        // StartReset 양방향: 가운데 원에 네모 꼬리 추가 (원래 시작 사각형 보존). 그 외에는 null.
        CenterTailGeometry = (IsBidirectional && ArrowType == ArrowType.StartReset)
            ? CreateCenterTailGeometry(renderPoints, MarkerSize)
            : null;

        var visualStart = renderPoints[0];
        var end = renderPoints[^1];
        StartX = visualStart.X;
        StartY = visualStart.Y;
        EndX = end.X;
        EndY = end.Y;
        CenterX = midpoint.X;
        CenterY = midpoint.Y;
        // _lastPoints는 항상 풀 패스 보관 (드래그 시 평행이동에 필요)
        _lastPoints = points;
    }

    private static (List<Point> First, List<Point> Second, Point Midpoint) SplitPathAtMidpoint(IReadOnlyList<Point> points)
    {
        if (points.Count == 4)
        {
            // 큐빅 베지어 De Casteljau t=0.5 분할
            var p0 = points[0]; var p1 = points[1]; var p2 = points[2]; var p3 = points[3];
            var m01 = MidPoint(p0, p1);
            var m12 = MidPoint(p1, p2);
            var m23 = MidPoint(p2, p3);
            var m012 = MidPoint(m01, m12);
            var m123 = MidPoint(m12, m23);
            var mid = MidPoint(m012, m123);
            return (
                new List<Point> { p0, m01, m012, mid },
                new List<Point> { mid, m123, m23, p3 },
                mid);
        }

        // 폴리라인: 누적 길이 절반 지점에서 분할
        var totalLength = 0.0;
        for (var i = 1; i < points.Count; i++)
            totalLength += (points[i] - points[i - 1]).Length;

        if (totalLength <= MinSegmentLength)
        {
            var only = points[0];
            return (new List<Point> { only }, new List<Point>(points), only);
        }

        var halfLength = totalLength * 0.5;
        var accumulated = 0.0;
        for (var i = 1; i < points.Count; i++)
        {
            var seg = points[i] - points[i - 1];
            var segLen = seg.Length;
            if (accumulated + segLen >= halfLength)
            {
                var t = segLen <= MinSegmentLength ? 0.0 : (halfLength - accumulated) / segLen;
                var midPt = new Point(points[i - 1].X + seg.X * t, points[i - 1].Y + seg.Y * t);
                var first = new List<Point>(i + 1);
                for (var j = 0; j < i; j++) first.Add(points[j]);
                first.Add(midPt);
                var second = new List<Point>(points.Count - i + 1) { midPt };
                for (var j = i; j < points.Count; j++) second.Add(points[j]);
                return (first, second, midPt);
            }
            accumulated += segLen;
        }

        var fallback = points[^1];
        return (new List<Point>(points), new List<Point> { fallback }, fallback);
    }

    private static Point MidPoint(Point a, Point b) => new((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5);

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

    /// <summary>
    /// 양방향 StartReset 쌍에서 가운데 원에 붙는 네모 꼬리. 단일 StartReset의 시작 사각형이 통합으로 사라지는 것을 시각적으로 보존.
    /// 라인 방향에 정렬된 정사각형을 midpoint 살짝 앞쪽(파트너 쪽 절반)으로 오프셋해 "꼬리" 느낌을 준다.
    /// </summary>
    private static Geometry CreateCenterTailGeometry(IReadOnlyList<Point> halfPoints, double size)
    {
        if (halfPoints.Count < 2)
            return Geometry.Empty;

        // halfPoints[0] = midpoint. midpoint 다음 점까지 방향이 leader의 forward 방향.
        Vector forward = default;
        for (var i = 1; i < halfPoints.Count; i++)
        {
            var seg = halfPoints[i] - halfPoints[i - 1];
            if (seg.Length > MinSegmentLength)
            {
                seg.Normalize();
                forward = seg;
                break;
            }
        }
        if (forward.LengthSquared < 0.5)
            return Geometry.Empty;

        // 꼬리는 leader의 forward 반대 방향(파트너 쪽)으로 오프셋해서, 원과 살짝 떨어진 곳에 위치.
        var tail = -forward;
        var perpendicular = new Vector(-tail.Y, tail.X);
        var midpoint = halfPoints[0];
        var sqHalf = size * 0.32;
        var sqCenter = midpoint + tail * (size * 0.55);

        var c1 = sqCenter + tail * sqHalf + perpendicular * sqHalf;
        var c2 = sqCenter + tail * sqHalf - perpendicular * sqHalf;
        var c3 = sqCenter - tail * sqHalf - perpendicular * sqHalf;
        var c4 = sqCenter - tail * sqHalf + perpendicular * sqHalf;

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(c1, true, true);
            ctx.LineTo(c2, true, false);
            ctx.LineTo(c3, true, false);
            ctx.LineTo(c4, true, false);
        }
        geo.Freeze();
        return geo;
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
