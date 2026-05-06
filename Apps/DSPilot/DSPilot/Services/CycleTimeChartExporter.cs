using System.Text;
using ClosedXML.Excel;
using DSPilot.Models.Analysis;

namespace DSPilot.Services;

/// <summary>
/// Cycle-Time Analysis 페이지의 CSV / Excel 내보내기.
/// 페이지 컴포넌트는 데이터만 넘기고 브라우저 다운로드는 cycle-time-chart.js 의 downloadFile 헬퍼 사용.
/// </summary>
public static class CycleTimeChartExporter
{
    public const string CsvMimeType = "text/csv;charset=utf-8";
    public const string XlsxMimeType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static byte[] BuildCsvBytes(GanttChartData data)
    {
        var sb = new StringBuilder();
        sb.AppendLine("CallName,WorkName,FlowName,TagName,TagAddress,EventType,GoingStartTime,FinishTime,Duration(ms),Lane");

        foreach (var item in data.Items.OrderBy(i => i.GoingStartTime))
        {
            var eventType = item.EventType == IOEventType.InTag ? "IN" : "OUT";
            var finish = item.FinishTime?.ToString("yyyy-MM-dd HH:mm:ss.fff") ?? "";
            var duration = item.Duration?.ToString() ?? "";
            sb.AppendLine(string.Join(",",
                CsvEscape(item.CallName),
                CsvEscape(item.WorkName),
                CsvEscape(item.FlowName),
                CsvEscape(item.TagName),
                CsvEscape(item.TagAddress),
                eventType,
                item.GoingStartTime.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                finish,
                duration,
                item.Lane));
        }

        // UTF-8 BOM 포함 — Excel 의 한글 깨짐 방지
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    public static string CsvFileName(string flowName)
        => $"CycleAnalysis_{flowName}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";

    public static string ExcelFileName(string flowName)
        => $"GanttChart_{flowName}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";

    public static byte[] BuildExcelBytes(
        GanttChartData data,
        IList<int> displayLaneOrder,
        IList<(DateTime Start, DateTime End)> idleRegions)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Gantt Chart");

        var chartStartTime = data.ActualEventStartTime ?? data.StartTime;
        var chartEndTime = data.ActualEventEndTime ?? (data.EndTime ?? data.StartTime.AddSeconds(1));
        var totalMs = (int)(chartEndTime - chartStartTime).TotalMilliseconds;

        int msPerCol = totalMs switch
        {
            <= 5_000 => 10,
            <= 30_000 => 100,
            <= 300_000 => 1000,
            _ => 5000
        };

        var totalCols = Math.Min((totalMs / msPerCol) + 1, 2000);
        const int dataStartCol = 4; // A=Call, B=Tag, C=Type, then time columns

        var palette = new ExcelPalette();

        BuildExcelTitleRow(ws, chartStartTime, chartEndTime, data, dataStartCol, totalCols);
        BuildExcelTimeAxis(ws, chartStartTime, msPerCol, totalCols, dataStartCol, palette);
        BuildExcelColumnWidths(ws, dataStartCol, totalCols);
        var idleColRanges = ComputeIdleColumnRanges(idleRegions, chartStartTime, msPerCol, totalCols);
        BuildExcelLaneRows(ws, data, displayLaneOrder, idleColRanges, chartStartTime, msPerCol, totalCols, dataStartCol, palette);
        BuildExcelSummarySheet(workbook, data, chartStartTime, chartEndTime, msPerCol, palette);
        BuildExcelDataSheet(workbook, data, palette);

        ws.SheetView.FreezeRows(3);
        ws.SheetView.FreezeColumns(3);
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.PagesWide = 1;
        ws.PageSetup.Header.Center.AddText($"{data.FlowName} - Cycle #{data.CycleNumber}");
        ws.PageSetup.Footer.Center.AddText($"Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private static string CsvEscape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    private sealed class ExcelPalette
    {
        public XLColor In { get; } = XLColor.FromHtml("#64B5F6");
        public XLColor Out { get; } = XLColor.FromHtml("#F48FB1");
        public XLColor HeaderBg { get; } = XLColor.FromHtml("#263238");
        public XLColor HeaderBg2 { get; } = XLColor.FromHtml("#37474F");
        public XLColor LaneHeaderBg { get; } = XLColor.FromHtml("#455A64");
        public XLColor SeparatorBg { get; } = XLColor.FromHtml("#ECEFF1");
        public XLColor IdleBg { get; } = XLColor.FromHtml("#FFEBEE");
        public XLColor StripeBg { get; } = XLColor.FromHtml("#FAFAFA");
    }

    private static void BuildExcelTitleRow(
        IXLWorksheet ws, DateTime chartStartTime, DateTime chartEndTime,
        GanttChartData data, int dataStartCol, int totalCols)
    {
        var titleText = $"{chartStartTime:yyyy-MM-dd (ddd)}  {chartStartTime:HH:mm:ss} ~ {chartEndTime:HH:mm:ss}  ({data.FlowName} Cycle #{data.CycleNumber})";
        ws.Cell(1, 1).Value = titleText;
        var titleEndCol = Math.Min(dataStartCol + totalCols - 1, dataStartCol + 40);
        ws.Range(1, 1, 1, titleEndCol).Merge();
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 12;
        ws.Cell(1, 1).Style.Font.FontColor = XLColor.Black;
        ws.Cell(1, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        ws.Cell(1, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Row(1).Height = 28;
    }

    private static void BuildExcelTimeAxis(
        IXLWorksheet ws, DateTime chartStartTime, int msPerCol, int totalCols, int dataStartCol,
        ExcelPalette palette)
    {
        ws.Cell(2, 1).Value = "Call";
        ws.Cell(2, 2).Value = "Tag";
        ws.Cell(2, 3).Value = "Type";
        ws.Range(2, 1, 3, 1).Merge().Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Range(2, 2, 3, 2).Merge().Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Range(2, 3, 3, 3).Merge().Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

        // Row 2: major time labels (per-second groups, merged)
        int mergeStart = 0;
        string lastSecLabel = "";
        for (int col = 0; col <= totalCols; col++)
        {
            var t = chartStartTime.AddMilliseconds(col * msPerCol);
            var secLabel = msPerCol < 1000 ? t.ToString("HH:mm:ss") : t.ToString("HH:mm");
            if (col == totalCols || secLabel != lastSecLabel)
            {
                if (col > mergeStart && !string.IsNullOrEmpty(lastSecLabel))
                {
                    var c1 = dataStartCol + mergeStart;
                    var c2 = dataStartCol + col - 1;
                    if (c2 > c1) ws.Range(2, c1, 2, c2).Merge();
                    ws.Cell(2, c1).Value = lastSecLabel;
                }
                mergeStart = col;
                lastSecLabel = secLabel;
            }
        }

        // Row 3: fine time labels
        for (int col = 0; col < totalCols; col++)
        {
            var t = chartStartTime.AddMilliseconds(col * msPerCol);
            var cell = ws.Cell(3, dataStartCol + col);
            cell.Value = msPerCol < 1000 ? t.ToString(".fff") : t.ToString(":ss");
        }

        for (int row = 2; row <= 3; row++)
        {
            var bg = row == 2 ? palette.HeaderBg : palette.HeaderBg2;
            var range = ws.Range(row, 1, row, dataStartCol + totalCols - 1);
            range.Style.Font.Bold = true;
            range.Style.Fill.BackgroundColor = bg;
            range.Style.Font.FontColor = XLColor.White;
            range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            range.Style.Font.FontSize = 8;
        }
        ws.Row(2).Height = 18;
        ws.Row(3).Height = 14;
    }

    private static void BuildExcelColumnWidths(IXLWorksheet ws, int dataStartCol, int totalCols)
    {
        for (int col = 0; col < totalCols; col++)
            ws.Column(dataStartCol + col).Width = 1.5;
        ws.Column(1).Width = 22;
        ws.Column(2).Width = 18;
        ws.Column(3).Width = 5;
    }

    private static List<(int Start, int End)> ComputeIdleColumnRanges(
        IList<(DateTime Start, DateTime End)> idleRegions,
        DateTime chartStartTime, int msPerCol, int totalCols)
    {
        return idleRegions.Select(r =>
        {
            var s = Math.Max(0, (int)((r.Start - chartStartTime).TotalMilliseconds / msPerCol));
            var e = Math.Min(totalCols - 1, (int)((r.End - chartStartTime).TotalMilliseconds / msPerCol));
            return (Start: s, End: e);
        }).Where(r => r.End >= r.Start).ToList();
    }

    private static void BuildExcelLaneRows(
        IXLWorksheet ws, GanttChartData data, IList<int> displayLaneOrder,
        List<(int Start, int End)> idleColRanges,
        DateTime chartStartTime, int msPerCol, int totalCols, int dataStartCol,
        ExcelPalette palette)
    {
        var itemsByLane = data.Items
            .GroupBy(i => i.Lane)
            .ToDictionary(g => g.Key, g => g.OrderBy(i => i.GoingStartTime).ToList());

        int currentRow = 4;
        foreach (var laneIdx in displayLaneOrder)
        {
            var laneLabel = laneIdx >= 0 && laneIdx < data.LaneLabels.Count
                ? data.LaneLabels[laneIdx]
                : $"Lane {laneIdx}";

            ws.Range(currentRow, 1, currentRow, 3).Merge();
            ws.Cell(currentRow, 1).Value = laneLabel;
            ws.Cell(currentRow, 1).Style.Font.Bold = true;
            ws.Cell(currentRow, 1).Style.Font.FontSize = 10;
            ws.Cell(currentRow, 1).Style.Font.FontColor = XLColor.White;
            ws.Range(currentRow, 1, currentRow, dataStartCol + totalCols - 1)
                .Style.Fill.BackgroundColor = palette.LaneHeaderBg;
            ws.Row(currentRow).Height = 20;
            currentRow++;

            if (!itemsByLane.TryGetValue(laneIdx, out var laneItems))
            {
                currentRow++;
                continue;
            }

            var inItems = laneItems.Where(i => i.EventType == IOEventType.InTag).OrderBy(i => i.GoingStartTime).ToList();
            var outItems = laneItems.Where(i => i.EventType == IOEventType.OutTag).OrderBy(i => i.GoingStartTime).ToList();

            foreach (var eventGroup in new[]
            {
                (Label: "IN",  Items: inItems,  Color: palette.In),
                (Label: "OUT", Items: outItems, Color: palette.Out)
            })
            {
                if (!eventGroup.Items.Any()) continue;

                var callGroups = eventGroup.Items.GroupBy(i => i.CallName).OrderBy(g => g.Min(i => i.GoingStartTime));

                foreach (var callGroup in callGroups)
                {
                    var firstItem = callGroup.First();
                    ws.Cell(currentRow, 1).Value = callGroup.Key;
                    ws.Cell(currentRow, 1).Style.Font.FontSize = 9;
                    ws.Cell(currentRow, 2).Value = firstItem.TagName;
                    ws.Cell(currentRow, 2).Style.Font.FontSize = 8;
                    ws.Cell(currentRow, 2).Style.Font.FontColor = XLColor.FromHtml("#78909C");
                    ws.Cell(currentRow, 3).Value = eventGroup.Label;
                    ws.Cell(currentRow, 3).Style.Font.FontSize = 8;
                    ws.Cell(currentRow, 3).Style.Font.FontColor = eventGroup.Label == "IN"
                        ? XLColor.FromHtml("#1565C0")
                        : XLColor.FromHtml("#AD1457");

                    foreach (var item in callGroup.OrderBy(i => i.GoingStartTime))
                    {
                        DrawExcelBar(ws, item, currentRow, dataStartCol, chartStartTime, msPerCol, totalCols, eventGroup.Color);
                    }

                    PaintIdleAndStripeBg(ws, currentRow, dataStartCol, totalCols, idleColRanges, chartStartTime, msPerCol, palette);

                    ws.Row(currentRow).Height = 16;
                    currentRow++;
                }
            }

            ws.Range(currentRow, 1, currentRow, dataStartCol + totalCols - 1)
                .Style.Fill.BackgroundColor = palette.SeparatorBg;
            ws.Row(currentRow).Height = 4;
            currentRow++;
        }
    }

    private static void DrawExcelBar(
        IXLWorksheet ws, GanttChartItem item, int row, int dataStartCol,
        DateTime chartStartTime, int msPerCol, int totalCols, XLColor color)
    {
        var itemStartMs = (item.GoingStartTime - chartStartTime).TotalMilliseconds;
        var itemEndMs = item.FinishTime.HasValue
            ? (item.FinishTime.Value - chartStartTime).TotalMilliseconds
            : itemStartMs + (item.Duration ?? msPerCol);

        var startCol = (int)(itemStartMs / msPerCol);
        var endCol = (int)(itemEndMs / msPerCol);
        startCol = Math.Max(0, Math.Min(startCol, totalCols - 1));
        endCol = Math.Max(startCol, Math.Min(endCol, totalCols - 1));
        if (endCol - startCol < 2) endCol = Math.Min(startCol + 2, totalCols - 1);

        for (int col = startCol; col <= endCol; col++)
        {
            ws.Cell(row, dataStartCol + col).Style.Fill.BackgroundColor = color;
        }

        var startCell = ws.Cell(row, dataStartCol + startCol);
        startCell.Value = item.GoingStartTime.ToString("mm:ss.fff");
        startCell.Style.Font.FontSize = 7;
        startCell.Style.Font.FontColor = XLColor.FromHtml("#1A237E");
        startCell.Style.Font.Bold = true;

        if (endCol > startCol + 1 && item.Duration.HasValue)
        {
            var endCell = ws.Cell(row, dataStartCol + endCol);
            endCell.Value = item.Duration.Value >= 1000
                ? $"({item.Duration.Value / 1000.0:F1}s)"
                : $"({item.Duration.Value}ms)";
            endCell.Style.Font.FontSize = 6;
            endCell.Style.Font.FontColor = XLColor.FromHtml("#1A237E");
        }

        if (endCol >= startCol)
        {
            var barRange = ws.Range(row, dataStartCol + startCol, row, dataStartCol + endCol);
            barRange.Style.Border.TopBorder = XLBorderStyleValues.Thin;
            barRange.Style.Border.TopBorderColor = color;
            barRange.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            barRange.Style.Border.BottomBorderColor = color;
            ws.Cell(row, dataStartCol + startCol).Style.Border.LeftBorder = XLBorderStyleValues.Thin;
            ws.Cell(row, dataStartCol + startCol).Style.Border.LeftBorderColor = color;
            ws.Cell(row, dataStartCol + endCol).Style.Border.RightBorder = XLBorderStyleValues.Thin;
            ws.Cell(row, dataStartCol + endCol).Style.Border.RightBorderColor = color;
        }
    }

    private static void PaintIdleAndStripeBg(
        IXLWorksheet ws, int row, int dataStartCol, int totalCols,
        List<(int Start, int End)> idleColRanges,
        DateTime chartStartTime, int msPerCol, ExcelPalette palette)
    {
        bool IsEmptyBg(IXLCell cell) =>
            cell.Style.Fill.BackgroundColor == XLColor.NoColor
            || cell.Style.Fill.BackgroundColor.Equals(XLColor.FromTheme(XLThemeColor.Background1));

        foreach (var idle in idleColRanges)
        {
            for (int col = idle.Start; col <= idle.End; col++)
            {
                var cell = ws.Cell(row, dataStartCol + col);
                if (IsEmptyBg(cell)) cell.Style.Fill.BackgroundColor = palette.IdleBg;
            }
        }

        for (int col = 0; col < totalCols; col++)
        {
            var t = chartStartTime.AddMilliseconds(col * msPerCol);
            var sec = msPerCol < 1000 ? t.Second : t.Minute;
            if (sec % 2 != 0) continue;
            var cell = ws.Cell(row, dataStartCol + col);
            if (IsEmptyBg(cell)) cell.Style.Fill.BackgroundColor = palette.StripeBg;
        }
    }

    private static void BuildExcelSummarySheet(
        XLWorkbook workbook, GanttChartData data,
        DateTime chartStartTime, DateTime chartEndTime, int msPerCol,
        ExcelPalette palette)
    {
        var summaryWs = workbook.Worksheets.Add("Summary");
        var rows = new (string Label, object Value)[]
        {
            ("Flow", data.FlowName),
            ("Cycle", data.CycleNumber),
            ("CT (ms)", data.CT ?? 0),
            ("MT (ms)", data.MT ?? 0),
            ("WT (ms)", data.WT ?? 0),
            ("Start", chartStartTime.ToString("yyyy-MM-dd HH:mm:ss.fff")),
            ("End", chartEndTime.ToString("yyyy-MM-dd HH:mm:ss.fff")),
            ("Resolution", $"{msPerCol}ms/col"),
        };
        for (int i = 0; i < rows.Length; i++)
        {
            summaryWs.Cell(i + 1, 1).Value = rows[i].Label;
            summaryWs.Cell(i + 1, 2).Value = XLCellValue.FromObject(rows[i].Value);
        }
        summaryWs.Column(1).Width = 15;
        summaryWs.Column(2).Width = 30;
        summaryWs.Range(1, 1, rows.Length, 1).Style.Font.Bold = true;

        // Legend
        summaryWs.Cell(rows.Length + 2, 1).Value = "Legend";
        summaryWs.Cell(rows.Length + 2, 1).Style.Font.Bold = true;
        var legend = new (string Label, XLColor Color, string Desc)[]
        {
            ("IN (InTag)", palette.In, "Call 시작 신호"),
            ("OUT (OutTag)", palette.Out, "Call 종료 신호"),
            ("비가동 구간", palette.IdleBg, "Cycle Time 초과 유휴 구간"),
        };
        for (int i = 0; i < legend.Length; i++)
        {
            var r = rows.Length + 3 + i;
            summaryWs.Cell(r, 1).Value = legend[i].Label;
            summaryWs.Cell(r, 1).Style.Fill.BackgroundColor = legend[i].Color;
            summaryWs.Cell(r, 2).Value = legend[i].Desc;
        }
    }

    private static void BuildExcelDataSheet(XLWorkbook workbook, GanttChartData data, ExcelPalette palette)
    {
        var dataWs = workbook.Worksheets.Add("Data");
        var headers = new[] { "CallName", "WorkName", "FlowName", "TagName", "TagAddress", "EventType", "GoingStartTime", "FinishTime", "Duration(ms)", "Lane" };
        for (int i = 0; i < headers.Length; i++)
        {
            dataWs.Cell(1, i + 1).Value = headers[i];
            dataWs.Cell(1, i + 1).Style.Font.Bold = true;
            dataWs.Cell(1, i + 1).Style.Fill.BackgroundColor = palette.HeaderBg2;
            dataWs.Cell(1, i + 1).Style.Font.FontColor = XLColor.White;
        }
        int dataRow = 2;
        foreach (var item in data.Items.OrderBy(i => i.GoingStartTime))
        {
            dataWs.Cell(dataRow, 1).Value = item.CallName;
            dataWs.Cell(dataRow, 2).Value = item.WorkName;
            dataWs.Cell(dataRow, 3).Value = item.FlowName;
            dataWs.Cell(dataRow, 4).Value = item.TagName;
            dataWs.Cell(dataRow, 5).Value = item.TagAddress;
            dataWs.Cell(dataRow, 6).Value = item.EventType == IOEventType.InTag ? "IN" : "OUT";
            dataWs.Cell(dataRow, 7).Value = item.GoingStartTime.ToString("yyyy-MM-dd HH:mm:ss.fff");
            dataWs.Cell(dataRow, 8).Value = item.FinishTime?.ToString("yyyy-MM-dd HH:mm:ss.fff") ?? "";
            dataWs.Cell(dataRow, 9).Value = item.Duration ?? 0;
            dataWs.Cell(dataRow, 10).Value = item.Lane;
            dataRow++;
        }
        dataWs.Columns().AdjustToContents();
        dataWs.SheetView.FreezeRows(1);
    }
}
