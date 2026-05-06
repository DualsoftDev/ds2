using System.Globalization;
using System.Text;
using DSPilot.Models.Analysis;

namespace DSPilot.Services;

/// <summary>
/// Cycle-Time Analysis 페이지의 base SVG (bars + axis + lanes + idle regions + defs) 마크업 생성기.
/// 데이터/정렬이 바뀐 시점에만 호출되어 SVG 문자열을 한 번 빌드하면, 이후 사용자 인터랙션
/// (선택/오버레이 토글) 은 별도 dynamic 레이어에서 처리되므로 base 는 다시 빌드되지 않는다.
/// 클릭은 컨테이너 단일 listener 가 <c>data-bar-idx</c> 로 위임하여 처리하므로 bar 마다
/// per-element EventCallback 을 만들지 않는다.
/// </summary>
public static class CycleTimeChartRenderer
{
    public const int LeftMargin = 220;
    public const int TopMargin = 60;
    public const int LaneHeight = 52;
    public const int BarHeight = 14;
    public const int PlotWidth = 2000;

    /// <summary>Base SVG 빌드 결과 — markup 과, dynamic overlay 가 좌표 계산에 쓸 geometry.</summary>
    public sealed record Result(
        string Svg,
        DateTime ChartStartTime,
        double XScale,
        Dictionary<int, int> DisplayLaneIndex,
        int ChartWidth,
        int ChartHeight);

    public static Result BuildBaseSvg(
        GanttChartData data,
        IList<GanttChartItem> sortedItems,
        IList<int> displayLaneOrder,
        IList<(DateTime Start, DateTime End)> idleRegions)
    {
        var chartStartTime = data.ActualEventStartTime ?? data.StartTime;
        var chartEndTime = data.ActualEventEndTime ?? (data.EndTime ?? data.StartTime.AddSeconds(1));
        var cycleTime = Math.Max((int)(chartEndTime - chartStartTime).TotalMilliseconds, 1);
        var xScale = PlotWidth / (double)cycleTime;

        var displayLaneIndex = new Dictionary<int, int>(displayLaneOrder.Count);
        for (int i = 0; i < displayLaneOrder.Count; i++)
            displayLaneIndex[displayLaneOrder[i]] = i;

        var chartWidth = LeftMargin + PlotWidth + 100;
        var chartHeight = TopMargin + (data.TotalLanes * LaneHeight) + 100;

        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(128 * 1024);

        // 외부 <svg> 태그 포함 — Blazor 가 통째로 갈아끼우게 해 SVG inner content 의 namespace diff 문제 회피
        sb.Append("<svg class=\"gantt-chart\" width=\"").Append(chartWidth)
          .Append("\" height=\"").Append(chartHeight)
          .Append("\" xmlns=\"http://www.w3.org/2000/svg\">");

        sb.Append("<rect class=\"gantt-bg\" width=\"100%\" height=\"100%\"/>");

        AppendTimeAxis(sb, chartStartTime, chartEndTime);

        AppendLanes(sb, data, displayLaneOrder);
        AppendBars(sb, sortedItems, displayLaneIndex, chartStartTime, xScale, inv);
        AppendIdleRegions(sb, idleRegions, chartStartTime, xScale, data.TotalLanes, inv);
        AppendDefs(sb);

        sb.Append("</svg>");

        return new Result(
            Svg: sb.ToString(),
            ChartStartTime: chartStartTime,
            XScale: xScale,
            DisplayLaneIndex: displayLaneIndex,
            ChartWidth: chartWidth,
            ChartHeight: chartHeight);
    }

    public static int GetItemDurationMs(GanttChartItem item)
    {
        if (item.Duration.HasValue && item.Duration.Value > 0) return item.Duration.Value;
        if (item.RelativeEnd.HasValue) return Math.Max(0, item.RelativeEnd.Value - item.RelativeStart);
        if (item.FinishTime.HasValue) return Math.Max(0, (int)(item.FinishTime.Value - item.GoingStartTime).TotalMilliseconds);
        return 0;
    }

    public static string FormatDurationTooltip(double durationMs) => $"{durationMs / 1000.0:F1} (sec)";

    public static string EscapeXml(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }

    // ─── Sub-builders ────────────────────────────────────────────────────────

    private static void AppendTimeAxis(StringBuilder sb, DateTime startTime, DateTime endTime)
    {
        const int tickCount = 20;
        var duration = (endTime - startTime).TotalMilliseconds;
        var tickInterval = duration / tickCount;

        sb.Append("<line class=\"gantt-axis\" x1=\"").Append(LeftMargin)
          .Append("\" y1=\"").Append(TopMargin - 5).Append("\" x2=\"").Append(LeftMargin + PlotWidth)
          .Append("\" y2=\"").Append(TopMargin - 5).Append("\"/>");

        for (int i = 0; i <= tickCount; i++)
        {
            var x = LeftMargin + (i * PlotWidth / tickCount);
            var tickTime = startTime.AddMilliseconds(i * tickInterval);

            sb.Append("<line class=\"gantt-tick\" x1=\"").Append(x).Append("\" y1=\"").Append(TopMargin - 5)
              .Append("\" x2=\"").Append(x).Append("\" y2=\"").Append(TopMargin - 10).Append("\"/>");
            sb.Append("<text class=\"gantt-tick-label\" x=\"").Append(x).Append("\" y=\"").Append(TopMargin - 15)
              .Append("\" text-anchor=\"middle\" font-size=\"10\">")
              .Append(tickTime.ToString("HH:mm:ss")).Append("</text>");
        }
    }

    private static void AppendLanes(StringBuilder sb, GanttChartData data, IList<int> displayLaneOrder)
    {
        for (int displayLane = 0; displayLane < displayLaneOrder.Count; displayLane++)
        {
            var lane = displayLaneOrder[displayLane];
            var y = TopMargin + (displayLane * LaneHeight);
            var laneClass = displayLane % 2 == 0 ? "gantt-lane-even" : "gantt-lane-odd";
            var laneCenterY = y + (LaneHeight / 2);
            var laneLabel = lane >= 0 && lane < data.LaneLabels.Count ? data.LaneLabels[lane] : $"Lane {lane}";

            sb.Append("<rect class=\"").Append(laneClass).Append("\" x=\"").Append(LeftMargin)
              .Append("\" y=\"").Append(y).Append("\" width=\"").Append(PlotWidth)
              .Append("\" height=\"").Append(LaneHeight).Append("\"/>");
            sb.Append("<line class=\"gantt-lane-center\" x1=\"").Append(LeftMargin)
              .Append("\" y1=\"").Append(laneCenterY).Append("\" x2=\"").Append(LeftMargin + PlotWidth)
              .Append("\" y2=\"").Append(laneCenterY).Append("\"/>");
            sb.Append("<text class=\"gantt-lane-label\" x=\"").Append(LeftMargin - 10)
              .Append("\" y=\"").Append(laneCenterY - 6)
              .Append("\" text-anchor=\"end\" dominant-baseline=\"middle\" font-size=\"12\" font-weight=\"600\">")
              .Append(EscapeXml(laneLabel)).Append("</text>");
            sb.Append("<text class=\"gantt-lane-sublabel\" x=\"").Append(LeftMargin - 10)
              .Append("\" y=\"").Append(laneCenterY + 10)
              .Append("\" text-anchor=\"end\" dominant-baseline=\"middle\" font-size=\"10\">IN / OUT</text>");
        }
    }

    private static void AppendBars(
        StringBuilder sb,
        IList<GanttChartItem> sortedItems,
        Dictionary<int, int> displayLaneIndex,
        DateTime chartStartTime,
        double xScale,
        CultureInfo inv)
    {
        for (int i = 0; i < sortedItems.Count; i++)
        {
            var item = sortedItems[i];
            var displayLane = displayLaneIndex.GetValueOrDefault(item.Lane, item.Lane);
            var laneTop = TopMargin + (displayLane * LaneHeight);
            var y = item.EventType == IOEventType.InTag ? laneTop + 7 : laneTop + 29;
            var x = LeftMargin + ((item.GoingStartTime - chartStartTime).TotalMilliseconds * xScale);
            var durationMs = GetItemDurationMs(item);
            var width = Math.Max(2, Math.Max(durationMs, 1) * xScale);

            var eventLabel = item.EventType == IOEventType.InTag ? "InTag" : "OutTag";
            var endLabel = item.FinishTime?.ToString("HH:mm:ss.fff") ?? "진행중";
            var tooltipText = $"Call: {item.CallName}\nWork: {item.WorkName}\nTag: {item.TagName} ({item.TagAddress})\nEvent: {eventLabel}\nStart: {item.GoingStartTime:HH:mm:ss.fff}\nEnd: {endLabel}\nDuration: {FormatDurationTooltip(durationMs)}";
            var barClass = item.EventType == IOEventType.InTag ? "gantt-bar-in" : "gantt-bar-out";

            sb.Append("<g style=\"cursor:pointer\" data-bar-idx=\"").Append(i).Append("\">")
              .Append("<title>").Append(EscapeXml(tooltipText)).Append("</title>")
              .Append("<rect class=\"").Append(barClass)
              .Append("\" x=\"").Append(x.ToString("F2", inv))
              .Append("\" y=\"").Append(y)
              .Append("\" width=\"").Append(width.ToString("F2", inv))
              .Append("\" height=\"").Append(BarHeight)
              .Append("\" rx=\"4\"/>");

            if (width > 30)
            {
                sb.Append("<text class=\"gantt-bar-label\" x=\"").Append((x + width / 2).ToString("F2", inv))
                  .Append("\" y=\"").Append(y + BarHeight / 2)
                  .Append("\" text-anchor=\"middle\" dominant-baseline=\"middle\" font-size=\"10\" style=\"pointer-events:none\">")
                  .Append(item.EventType == IOEventType.InTag ? "In" : "Out")
                  .Append("</text>");
            }

            sb.Append("</g>");
        }
    }

    private static void AppendIdleRegions(
        StringBuilder sb,
        IList<(DateTime Start, DateTime End)> idleRegions,
        DateTime chartStartTime,
        double xScale,
        int totalLanes,
        CultureInfo inv)
    {
        foreach (var (idleStart, idleEnd) in idleRegions)
        {
            double idleX = LeftMargin + Math.Max(0, (idleStart - chartStartTime).TotalMilliseconds * xScale);
            double idleW = (idleEnd - idleStart).TotalMilliseconds * xScale;
            if (idleX + idleW < LeftMargin || idleX > LeftMargin + PlotWidth) continue;
            if (idleX < LeftMargin) { idleW -= (LeftMargin - idleX); idleX = LeftMargin; }
            if (idleX + idleW > LeftMargin + PlotWidth) idleW = LeftMargin + PlotWidth - idleX;

            sb.Append("<rect class=\"gantt-idle-region\" x=\"").Append(idleX.ToString("F2", inv))
              .Append("\" y=\"").Append(TopMargin)
              .Append("\" width=\"").Append(idleW.ToString("F2", inv))
              .Append("\" height=\"").Append(totalLanes * LaneHeight).Append("\"/>");

            if (idleW > 30)
            {
                sb.Append("<text class=\"gantt-idle-label\" x=\"").Append((idleX + idleW / 2).ToString("F2", inv))
                  .Append("\" y=\"").Append(TopMargin + 16)
                  .Append("\" text-anchor=\"middle\" dominant-baseline=\"middle\">비가동</text>");
            }
        }
    }

    private static void AppendDefs(StringBuilder sb)
    {
        sb.Append("<defs>")
          .Append("<marker id=\"cycleTimeArrowOrange\" markerWidth=\"10\" markerHeight=\"10\" refX=\"9\" refY=\"3\" orient=\"auto\"><polygon class=\"gantt-arrow-orange\" points=\"0 0, 10 3, 0 6\"/></marker>")
          .Append("<marker id=\"cycleTimeArrowTeal\" markerWidth=\"10\" markerHeight=\"10\" refX=\"9\" refY=\"3\" orient=\"auto\"><polygon class=\"gantt-arrow-teal\" points=\"0 0, 10 3, 0 6\"/></marker>")
          .Append("<marker id=\"cycleTimeArrowPurple\" markerWidth=\"10\" markerHeight=\"10\" refX=\"9\" refY=\"3\" orient=\"auto\"><polygon class=\"gantt-arrow-purple\" points=\"0 0, 10 3, 0 6\"/></marker>")
          .Append("</defs>");
    }
}
