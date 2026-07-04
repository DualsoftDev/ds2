// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Globalization;
using System.Text;
using ClosedXML.Excel;

namespace DSPilot.Services;

/// <summary>
/// Cycle-Time Analysis 페이지의 내보내기.
///
/// Excel(.xlsx) 은 <see cref="BuildCycleAnalysisExcel"/> 로 생성한다 — 서버에서 데이터를 재계산하지 않고,
/// 클라이언트(정적 cycle-time-analysis.html)가 화면에 그린 현재 상태(<see cref="CycleExcelModel"/>: 정렬된 lane +
/// 병합 intervals + Head/Tail + 보기모드 + 사이클 경계/Tail 마커 + 활성 Gap)를 그대로 받아 렌더한다.
/// → 화면 간트와 1:1 (WYSIWYG). Sheet1 = 간트 재현(셀 그리드), Sheet2 = 신호 세그먼트 + 사이클 요약 데이터 테이블.
/// </summary>
public static class CycleTimeChartExporter
{
    public const string XlsxMimeType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    // ─── Excel (WYSIWYG — 화면 모델 기반) ────────────────────────────────────────

    /// <summary>
    /// 화면이 그린 현재 상태(<paramref name="model"/>)를 그대로 .xlsx 로 렌더. 두 시트:
    ///   Sheet1 "간트차트" — 시간 그리드 셀에 lane별 막대/신호 + 사이클 리본 + 경계선 + Head/Tail + 활성 Gap 재현.
    ///   Sheet2 "데이터"   — 신호 세그먼트 표 + 사이클 요약 표.
    /// </summary>
    public static byte[] BuildCycleAnalysisExcel(CycleExcelModel model)
    {
        using var workbook = new XLWorkbook();
        var palette = new CycleExcelPalette();
        var ws = workbook.Worksheets.Add("간트차트");
        BuildGanttSheet(ws, model, palette, 1, applySheetChrome: true);
        BuildDataSheet(workbook, new[] { model }, palette, includeFlow: false);

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// 전체 편집(bulkCycleApp) Excel — 여러 Flow 의 화면 간트를 한 시트("간트차트")에 위→아래로 이어 쌓는다.
    /// 각 Flow 블록은 <see cref="BuildGanttSheet"/> 를 baseRow 를 누적하며 호출해 렌더(제목행이 Flow명 헤더 역할).
    /// 블록마다 자체 시간축을 가지므로 열 의미는 블록마다 다르다(세로 나열 = 화면 카드 나열과 1:1).
    /// Sheet2 "데이터" 는 단일 내보내기와 동일 테이블에 Flow 열을 더해 모든 Flow 를 세로로 쌓는다.
    /// </summary>
    public static byte[] BuildBulkCycleAnalysisExcel(IReadOnlyList<CycleExcelModel> models)
    {
        using var workbook = new XLWorkbook();
        var palette = new CycleExcelPalette();
        var ws = workbook.Worksheets.Add("간트차트");

        int row = 1;
        var rendered = new List<CycleExcelModel>();
        foreach (var m in models ?? new List<CycleExcelModel>())
        {
            if (m is null || (m.Lanes?.Count ?? 0) == 0) continue;
            int last = BuildGanttSheet(ws, m, palette, row, applySheetChrome: false);
            row = last + 3;   // 블록 사이 여백 2~3행
            rendered.Add(m);
        }

        if (rendered.Count == 0)
        {
            ws.Cell(1, 1).Value = "내보낼 간트가 없습니다.";
        }
        else
        {
            ws.SheetView.FreezeColumns(2);
            ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
            ws.PageSetup.Footer.Center.AddText($"Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            BuildDataSheet(workbook, rendered, palette, includeFlow: true);
        }

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }

    // ── Sheet1: 간트 재현 (baseRow 부터 렌더 · 여러 번 호출해 세로 쌓기 가능) ──────────────
    //   반환값 = 이 블록이 사용한 마지막 행(다음 블록의 baseRow 계산용).
    //   applySheetChrome=true 일 때만 FreezeRows/PageSetup/헤더푸터를 적용(단일 시트 전용).
    private static int BuildGanttSheet(IXLWorksheet ws, CycleExcelModel model, CycleExcelPalette p, int baseRow, bool applySheetChrome)
    {
        var lanes = model.Lanes ?? new List<CycleExcelLane>();
        var boundaries = (model.CycleBoundaries ?? new List<string>());

        long chartStartMs = EpochMs(model.ChartStart);
        long chartEndMs = EpochMs(model.ChartEnd);
        if (chartEndMs <= chartStartMs) chartEndMs = chartStartMs + 1000;
        long totalMs = Math.Max(1, chartEndMs - chartStartMs);
        var startWall = Wall(model.ChartStart);

        // 칸 해상도 — 화면처럼 컨테이너 폭에 맞추는 대신 시간당 ms 로 양자화. cap 를 넘으면 해상도를 낮춰
        // (msPerCol↑) 전체 구간을 빠짐없이 덮는다(구 버전이 열 cap 으로 뒤 구간을 잘라먹던 버그 방지).
        const int cap = 1000;
        int msPerCol = totalMs switch
        {
            <= 5_000 => 10,
            <= 30_000 => 100,
            <= 300_000 => 1000,
            _ => 5000
        };
        if (totalMs / msPerCol + 1 > cap)
            msPerCol = (int)Math.Ceiling((double)totalMs / cap);
        int totalCols = (int)(totalMs / msPerCol) + 1;

        const int firstDataCol = 3;                       // A=Call, B=Work, C..=시간
        int lastDataCol = firstDataCol + totalCols - 1;

        int ColOf(long ms) => Math.Max(0, Math.Min((int)((ms - chartStartMs) / msPerCol), totalCols - 1));
        int ColOfOff(double offMs) => Math.Max(0, Math.Min((int)(offMs / msPerCol), totalCols - 1));

        var bnd = boundaries
            .Select(s => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUnixTimeMilliseconds())
            .OrderBy(x => x).ToList();
        var tails = (model.TailEdges ?? new List<string>())
            .Select(EpochMs).OrderBy(x => x).ToList();
        bool hasCycles = bnd.Count > 0;
        var spans = hasCycles ? BuildSpans(bnd, tails, chartEndMs) : new List<CycleSpan>();

        int rowTitle = baseRow, rowMajor = baseRow + 1, rowFine = baseRow + 2;
        int rowRibbon = hasCycles ? baseRow + 3 : 0;
        int firstLaneRow = hasCycles ? baseRow + 4 : baseRow + 3;
        int lastLaneRow = lanes.Count > 0 ? firstLaneRow + lanes.Count - 1 : firstLaneRow;
        bool bar = string.Equals(model.ViewMode, "bar", StringComparison.OrdinalIgnoreCase);

        bool IsHead(CycleExcelLane l) => !string.IsNullOrEmpty(model.HeadCallId) && l.CallId == model.HeadCallId;
        bool IsTail(CycleExcelLane l) => !string.IsNullOrEmpty(model.TailCallId) && l.CallId == model.TailCallId;

        // 1) 제목
        var title = new StringBuilder($"{model.FlowName}    {startWall:yyyy-MM-dd HH:mm:ss} ~ {Wall(model.ChartEnd):HH:mm:ss}");
        if (!string.IsNullOrEmpty(model.HeadName))
            title.Append($"    ·  Head {model.HeadName}{(string.IsNullOrEmpty(model.TailName) ? "" : $" → Tail {model.TailName}")}");
        if (model.AvgCycleMs.HasValue) title.Append($"    ·  CT평균 {FormatMs(model.AvgCycleMs)}");
        if (model.AvgActiveMs.HasValue) title.Append($"  활성평균 {FormatMs(model.AvgActiveMs)}");
        title.Append($"    ·  {msPerCol}ms/칸");
        ws.Cell(rowTitle, 1).Value = title.ToString();
        ws.Range(rowTitle, 1, rowTitle, Math.Max(2, Math.Min(lastDataCol, firstDataCol + 60))).Merge();
        ws.Cell(rowTitle, 1).Style.Font.Bold = true;
        ws.Cell(rowTitle, 1).Style.Font.FontSize = 12;
        ws.Cell(rowTitle, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Row(rowTitle).Height = 26;

        // 2) 시간축 — major(병합) + fine
        ws.Cell(rowMajor, 1).Value = "Call";
        ws.Cell(rowMajor, 2).Value = "Work";
        ws.Range(rowMajor, 1, rowFine, 1).Merge().Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Range(rowMajor, 2, rowFine, 2).Merge().Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

        int mergeStart = 0;
        string lastLabel = "";
        for (int col = 0; col <= totalCols; col++)
        {
            var t = startWall.AddMilliseconds((double)col * msPerCol);
            var label = msPerCol < 1000 ? t.ToString("HH:mm:ss") : t.ToString("HH:mm");
            if (col == totalCols || label != lastLabel)
            {
                if (col > mergeStart && !string.IsNullOrEmpty(lastLabel))
                {
                    int c1 = firstDataCol + mergeStart, c2 = firstDataCol + col - 1;
                    if (c2 > c1) ws.Range(rowMajor, c1, rowMajor, c2).Merge();
                    ws.Cell(rowMajor, c1).Value = lastLabel;
                }
                mergeStart = col;
                lastLabel = label;
            }
        }
        for (int col = 0; col < totalCols; col++)
        {
            var t = startWall.AddMilliseconds((double)col * msPerCol);
            ws.Cell(rowFine, firstDataCol + col).Value = msPerCol < 1000 ? t.ToString(".fff") : t.ToString(":ss");
        }
        foreach (var (row, bg) in new[] { (rowMajor, p.HeaderBg), (rowFine, p.HeaderBg2) })
        {
            var range = ws.Range(row, 1, row, lastDataCol);
            range.Style.Font.Bold = true;
            range.Style.Fill.BackgroundColor = bg;
            range.Style.Font.FontColor = XLColor.White;
            range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            range.Style.Font.FontSize = 8;
        }
        ws.Row(rowMajor).Height = 16;
        ws.Row(rowFine).Height = 13;

        // 3) 사이클 리본 — [활성 초록 | 유휴 회청] + #N + 가동률 (화면 appendCycleRibbon)
        if (hasCycles)
        {
            ws.Range(rowRibbon, 1, rowRibbon, 2).Merge();
            ws.Cell(rowRibbon, 1).Value = "사이클";
            ws.Cell(rowRibbon, 1).Style.Font.Bold = true;
            ws.Cell(rowRibbon, 1).Style.Font.FontSize = 9;
            ws.Range(rowRibbon, firstDataCol, rowRibbon, lastDataCol).Style.Fill.BackgroundColor = p.RibbonTrack;
            foreach (var sp in spans)
            {
                int sCol = ColOf(sp.Start), eCol = ColOf(sp.End);
                if (sp.TailIn.HasValue)
                {
                    int tCol = ColOf(sp.TailIn.Value);
                    if (tCol > sCol)
                        ws.Range(rowRibbon, firstDataCol + sCol, rowRibbon, firstDataCol + tCol - 1).Style.Fill.BackgroundColor = p.RibbonActive;
                    ws.Range(rowRibbon, firstDataCol + tCol, rowRibbon, firstDataCol + eCol).Style.Fill.BackgroundColor = p.RibbonIdle;
                }
                else
                {
                    ws.Range(rowRibbon, firstDataCol + sCol, rowRibbon, firstDataCol + eCol).Style.Fill.BackgroundColor =
                        sp.Number % 2 == 0 ? p.RibbonAltA : p.RibbonAltB;
                }

                var numCell = ws.Cell(rowRibbon, firstDataCol + sCol);
                numCell.Value = sp.IsOpen ? $"#{sp.Number}↻" : $"#{sp.Number}";
                numCell.Style.Font.Bold = true;
                numCell.Style.Font.FontSize = 8;
                numCell.Style.Font.FontColor = p.Ink;

                if (sp.TailIn.HasValue && eCol - sCol > 6)
                {
                    long ct = sp.End - sp.Start, at = sp.TailIn.Value - sp.Start;
                    int ratio = ct > 0 ? (int)Math.Round(at * 100.0 / ct) : 0;
                    var rc = ws.Cell(rowRibbon, firstDataCol + Math.Max(sCol + 1, eCol - 4));
                    rc.Value = $"{ratio}%";
                    rc.Style.Font.FontSize = 8;
                    rc.Style.Font.Bold = true;
                    rc.Style.Font.FontColor = ratio >= 80 ? p.Good : ratio >= 50 ? p.Mid : p.Low;
                    rc.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                }
            }
            ws.Row(rowRibbon).Height = 18;
        }

        // 4) lane 라벨 + Head/Tail 틴트
        for (int i = 0; i < lanes.Count; i++)
        {
            var lane = lanes[i];
            int r = firstLaneRow + i;
            bool head = IsHead(lane), tail = IsTail(lane);

            var nameCell = ws.Cell(r, 1);
            nameCell.Value = lane.CallName + (head ? "  ▶H" : tail ? "  ▶T" : "");
            nameCell.Style.Font.FontSize = 9;
            nameCell.Style.Font.Bold = head || tail;
            var workCell = ws.Cell(r, 2);
            workCell.Value = lane.WorkName ?? "";
            workCell.Style.Font.FontSize = 8;
            workCell.Style.Font.FontColor = p.SubInk;
            if (head || tail)
            {
                ws.Range(r, 1, r, 2).Style.Fill.BackgroundColor = head ? p.HeadTint : p.TailTint;
                nameCell.Style.Border.LeftBorder = XLBorderStyleValues.Thick;
                nameCell.Style.Border.LeftBorderColor = head ? p.Head : p.Tail;
            }
            ws.Row(r).Height = 16;
        }

        // 5) 사이클 band (레인 뒤 옅은 컬럼 음영) — 막대보다 먼저 칠해 막대가 덮도록
        if (hasCycles && lanes.Count > 0)
        {
            foreach (var sp in spans)
            {
                int sCol = ColOf(sp.Start), eCol = ColOf(sp.End);
                if (sp.TailIn.HasValue)
                {
                    int tCol = ColOf(sp.TailIn.Value);
                    if (tCol > sCol)
                        ws.Range(firstLaneRow, firstDataCol + sCol, lastLaneRow, firstDataCol + tCol - 1).Style.Fill.BackgroundColor = p.BandActive;
                    ws.Range(firstLaneRow, firstDataCol + tCol, lastLaneRow, firstDataCol + eCol).Style.Fill.BackgroundColor = p.BandIdle;
                }
                else
                {
                    ws.Range(firstLaneRow, firstDataCol + sCol, lastLaneRow, firstDataCol + eCol).Style.Fill.BackgroundColor =
                        sp.Number % 2 == 0 ? p.BandAltA : p.BandAltB;
                }
            }
        }

        // 6) 막대/신호 — bar=합집합 솔리드, line=OUT(파랑)/IN(주황) 신호 (화면 색 그대로)
        for (int i = 0; i < lanes.Count; i++)
        {
            var lane = lanes[i];
            int r = firstLaneRow + i;
            if (bar)
            {
                var fill = IsHead(lane) ? p.Head : IsTail(lane) ? p.Tail : p.Union;
                foreach (var iv in lane.Intervals ?? new List<CycleExcelInterval>())
                    FillBar(ws, r, firstDataCol, ColOf(EpochMs(iv.Start)), ColOf(EpochMs(iv.End)), fill);
            }
            else
            {
                foreach (var iv in lane.OutIntervals ?? new List<CycleExcelInterval>())
                    FillBar(ws, r, firstDataCol, ColOf(EpochMs(iv.Start)), ColOf(EpochMs(iv.End)), p.Out);
                foreach (var iv in lane.InIntervals ?? new List<CycleExcelInterval>())
                    FillBar(ws, r, firstDataCol, ColOf(EpochMs(iv.Start)), ColOf(EpochMs(iv.End)), p.In);
            }
        }

        // 7) 활성 Gap 강조 (화면과 동일 — showMaxGap + 선택 인덱스 1개)
        var topGaps = model.TopGaps ?? new List<CycleExcelGap>();
        if (model.ShowMaxGap && topGaps.Count > 0)
        {
            int gi = model.SelectedGapIndex >= 0 && model.SelectedGapIndex < topGaps.Count ? model.SelectedGapIndex : 0;
            var gap = topGaps[gi];
            int laneIdx = lanes.FindIndex(l => l.CallId == gap.CallId);
            if (laneIdx >= 0)
            {
                int r = firstLaneRow + laneIdx;
                int gs = ColOfOff(gap.StartOffMs), ge = Math.Max(ColOfOff(gap.EndOffMs), ColOfOff(gap.StartOffMs));
                var gr = ws.Range(r, firstDataCol + gs, r, firstDataCol + ge);
                gr.Style.Fill.BackgroundColor = p.GapFill;
                gr.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
                gr.Style.Border.OutsideBorderColor = p.GapBorder;
                var gc = ws.Cell(r, firstDataCol + gs);
                gc.Value = $"⚠ {FormatMs(gap.DurMs)}";
                gc.Style.Font.FontSize = 7;
                gc.Style.Font.Bold = true;
                gc.Style.Font.FontColor = p.GapBorder;
            }
        }

        // 8) 사이클 경계선(주황) + Tail 분할선(보라) — 막대 위 세로 좌측 보더
        if (hasCycles && lanes.Count > 0)
        {
            int topRow = rowRibbon > 0 ? rowRibbon : firstLaneRow;
            foreach (var b in bnd)
            {
                int c = ColOf(b);
                var line = ws.Range(topRow, firstDataCol + c, lastLaneRow, firstDataCol + c);
                line.Style.Border.LeftBorder = XLBorderStyleValues.Medium;
                line.Style.Border.LeftBorderColor = p.Boundary;
            }
            foreach (var sp in spans.Where(s => s.TailIn.HasValue))
            {
                int c = ColOf(sp.TailIn!.Value);
                var line = ws.Range(firstLaneRow, firstDataCol + c, lastLaneRow, firstDataCol + c);
                line.Style.Border.LeftBorder = XLBorderStyleValues.Dashed;
                line.Style.Border.LeftBorderColor = p.Tail;
            }
        }

        // 9) 폭/고정/페이지
        ws.Column(1).Width = 22;
        ws.Column(2).Width = 14;
        ws.Columns(firstDataCol, lastDataCol).Width = 1.6;
        if (applySheetChrome)
        {
            ws.SheetView.FreezeRows(hasCycles ? 4 : 3);
            ws.SheetView.FreezeColumns(2);
            ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
            ws.PageSetup.Header.Center.AddText(model.FlowName);
            ws.PageSetup.Footer.Center.AddText($"Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        }

        return lastLaneRow;
    }

    // ── Sheet2: 데이터 테이블 (단일 = Flow 열 없음 · 전체 편집 = Flow 열을 더해 모든 Flow 세로 스택) ──
    private static void BuildDataSheet(XLWorkbook workbook, IReadOnlyList<CycleExcelModel> models, CycleExcelPalette p, bool includeFlow)
    {
        var ws = workbook.Worksheets.Add("데이터");
        int off = includeFlow ? 1 : 0;   // Flow 열 유무에 따른 열 오프셋

        // 1) 신호 세그먼트 표 — 화면 간트가 이 세그먼트들로 그려진다(같은 원천 → 표·그래프 일치).
        var headers = new List<string>();
        if (includeFlow) headers.Add("Flow");
        headers.AddRange(new[] { "Call", "Work", "신호", "Tag", "시작", "종료", "지속(ms)" });
        for (int i = 0; i < headers.Count; i++)
        {
            var c = ws.Cell(1, i + 1);
            c.Value = headers[i];
            c.Style.Font.Bold = true;
            c.Style.Fill.BackgroundColor = p.HeaderBg2;
            c.Style.Font.FontColor = XLColor.White;
        }

        int row = 2;
        foreach (var model in models)
        {
            var rows = new List<(string Call, string Work, string Kind, string Tag, DateTime Start, DateTime End, long Dur)>();
            foreach (var lane in model.Lanes ?? new List<CycleExcelLane>())
            {
                foreach (var iv in lane.OutIntervals ?? new List<CycleExcelInterval>())
                    rows.Add((lane.CallName, lane.WorkName ?? "", "OUT", lane.OutTag ?? "", Wall(iv.Start), Wall(iv.End), EpochMs(iv.End) - EpochMs(iv.Start)));
                foreach (var iv in lane.InIntervals ?? new List<CycleExcelInterval>())
                    rows.Add((lane.CallName, lane.WorkName ?? "", "IN", lane.InTag ?? "", Wall(iv.Start), Wall(iv.End), EpochMs(iv.End) - EpochMs(iv.Start)));
            }

            foreach (var x in rows.OrderBy(x => x.Start))
            {
                if (includeFlow) ws.Cell(row, 1).Value = model.FlowName;
                ws.Cell(row, off + 1).Value = x.Call;
                ws.Cell(row, off + 2).Value = x.Work;
                ws.Cell(row, off + 3).Value = x.Kind;
                ws.Cell(row, off + 3).Style.Font.Bold = true;
                ws.Cell(row, off + 3).Style.Font.FontColor = x.Kind == "OUT" ? p.Out : p.In;
                ws.Cell(row, off + 4).Value = x.Tag;
                ws.Cell(row, off + 5).Value = x.Start.ToString("HH:mm:ss.fff");
                ws.Cell(row, off + 6).Value = x.End.ToString("HH:mm:ss.fff");
                ws.Cell(row, off + 7).Value = x.Dur;
                row++;
            }
        }

        // 고정 너비 — ClosedXML AdjustToContents 의 한글 폭 버그 회피(메모리 규칙).
        if (includeFlow) ws.Column(1).Width = 18;
        ws.Column(off + 1).Width = 22;
        ws.Column(off + 2).Width = 18;
        ws.Column(off + 3).Width = 6;
        ws.Column(off + 4).Width = 24;
        ws.Column(off + 5).Width = 14;
        ws.Column(off + 6).Width = 14;
        ws.Column(off + 7).Width = 11;
        ws.SheetView.FreezeRows(1);

        // 2) 사이클 요약 표 (화면 '사이클 목록' 과 동일) — 세그먼트 표 오른쪽에 한 열 띄우고 시작.
        int c0 = headers.Count + 2;
        var sh = new List<string>();
        if (includeFlow) sh.Add("Flow");
        sh.AddRange(new[] { "#", "시작", "AT(ms)", "CT(ms)", "가동률%" });
        bool anySummary = false;
        int rr = 2;
        foreach (var model in models)
        {
            var bndDto = (model.CycleBoundaries ?? new List<string>())
                .Select(s => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))
                .OrderBy(x => x).ToList();
            if (bndDto.Count == 0) continue;

            if (!anySummary)
            {
                for (int i = 0; i < sh.Count; i++)
                {
                    var c = ws.Cell(1, c0 + i);
                    c.Value = sh[i];
                    c.Style.Font.Bold = true;
                    c.Style.Fill.BackgroundColor = p.HeaderBg2;
                    c.Style.Font.FontColor = XLColor.White;
                }
                anySummary = true;
            }

            var bnd = bndDto.Select(x => x.ToUnixTimeMilliseconds()).ToList();
            var tails = (model.TailEdges ?? new List<string>()).Select(EpochMs).OrderBy(x => x).ToList();
            var spans = BuildSpans(bnd, tails, EpochMs(model.ChartEnd));

            foreach (var sp in spans)
            {
                long ct = sp.End - sp.Start;
                long? at = sp.TailIn.HasValue ? sp.TailIn.Value - sp.Start : (long?)null;
                int? ratio = (at.HasValue && ct > 0) ? (int)Math.Round(at.Value * 100.0 / ct) : (int?)null;

                if (includeFlow) ws.Cell(rr, c0).Value = model.FlowName;
                ws.Cell(rr, c0 + off).Value = sp.IsOpen ? $"#{sp.Number}↻" : $"#{sp.Number}";
                ws.Cell(rr, c0 + off + 1).Value = bndDto[sp.Number - 1].DateTime.ToString("HH:mm:ss");
                if (at.HasValue) ws.Cell(rr, c0 + off + 2).Value = at.Value;
                else ws.Cell(rr, c0 + off + 2).Value = "—";
                ws.Cell(rr, c0 + off + 3).Value = ct;
                if (ratio.HasValue)
                {
                    var rc = ws.Cell(rr, c0 + off + 4);
                    rc.Value = ratio.Value;
                    rc.Style.Font.Bold = true;
                    rc.Style.Font.FontColor = ratio.Value >= 80 ? p.Good : ratio.Value >= 50 ? p.Mid : p.Low;
                }
                else ws.Cell(rr, c0 + off + 4).Value = "—";
                rr++;
            }
        }

        if (anySummary)
        {
            if (includeFlow) ws.Column(c0).Width = 18;
            ws.Column(c0 + off).Width = 6;
            ws.Column(c0 + off + 1).Width = 12;
            ws.Column(c0 + off + 2).Width = 11;
            ws.Column(c0 + off + 3).Width = 11;
            ws.Column(c0 + off + 4).Width = 9;
        }
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────

    private static void FillBar(IXLWorksheet ws, int row, int firstDataCol, int startCol, int endCol, XLColor color)
    {
        if (endCol < startCol) endCol = startCol;
        ws.Range(row, firstDataCol + startCol, row, firstDataCol + endCol).Style.Fill.BackgroundColor = color;
    }

    /// <summary>cycleBoundaries(+ open span to chartEnd) → 사이클 구간. 각 구간의 첫 Tail(InTag↑) 부착. 화면 cycleList 와 동일.</summary>
    private static List<CycleSpan> BuildSpans(IReadOnlyList<long> bnd, IReadOnlyList<long> tails, long chartEndMs)
    {
        var spans = new List<CycleSpan>();
        for (int i = 0; i < bnd.Count - 1; i++)
            spans.Add(new CycleSpan(bnd[i], bnd[i + 1], i + 1, false));
        if (bnd.Count > 0 && bnd[^1] < chartEndMs)
            spans.Add(new CycleSpan(bnd[^1], chartEndMs, bnd.Count, true));

        int ti = 0;
        foreach (var sp in spans)
        {
            while (ti < tails.Count && tails[ti] <= sp.Start) ti++;
            if (ti < tails.Count && tails[ti] < sp.End) sp.TailIn = tails[ti];
        }
        return spans;
    }

    private static long EpochMs(string iso)
        => DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUnixTimeMilliseconds();

    /// <summary>ISO 의 벽시계(작성된 로컬 시각) — 라벨용. offset 이 박혀 있어도 표기된 시:분:초 그대로.</summary>
    private static DateTime Wall(string iso)
        => DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).DateTime;

    private static string FormatMs(double? ms)
    {
        if (ms is null || ms <= 0) return "-";
        var v = ms.Value;
        if (v < 1000) return $"{Math.Round(v)} ms";
        if (v < 60000) return (v / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + " s";
        return (v / 60000.0).ToString("0.00", CultureInfo.InvariantCulture) + " min";
    }

    private sealed class CycleSpan
    {
        public long Start { get; }
        public long End { get; }
        public int Number { get; }
        public bool IsOpen { get; }
        public long? TailIn { get; set; }
        public CycleSpan(long start, long end, int number, bool isOpen)
        {
            Start = start; End = end; Number = number; IsOpen = isOpen;
        }
    }

    // 색은 화면 SVG 리터럴과 1:1 (캔버스는 항상 흰 배경 기준).
    private sealed class CycleExcelPalette
    {
        public XLColor Union { get; } = XLColor.FromHtml("#5B9BD5");
        public XLColor Head { get; } = XLColor.FromHtml("#4CAF50");
        public XLColor Tail { get; } = XLColor.FromHtml("#AB47BC");
        public XLColor Out { get; } = XLColor.FromHtml("#1E88E5");
        public XLColor In { get; } = XLColor.FromHtml("#FB8C00");
        public XLColor RibbonTrack { get; } = XLColor.FromHtml("#FAFBFC");
        public XLColor RibbonActive { get; } = XLColor.FromHtml("#81C784");
        public XLColor RibbonIdle { get; } = XLColor.FromHtml("#B0BEC5");
        public XLColor RibbonAltA { get; } = XLColor.FromHtml("#9FA8DA");
        public XLColor RibbonAltB { get; } = XLColor.FromHtml("#CE93D8");
        public XLColor BandActive { get; } = XLColor.FromHtml("#EAF6EA");
        public XLColor BandIdle { get; } = XLColor.FromHtml("#ECEFF1");
        public XLColor BandAltA { get; } = XLColor.FromHtml("#EDEFF8");
        public XLColor BandAltB { get; } = XLColor.FromHtml("#F6EDF8");
        public XLColor HeadTint { get; } = XLColor.FromHtml("#E8F5E9");
        public XLColor TailTint { get; } = XLColor.FromHtml("#F3E5F5");
        public XLColor GapFill { get; } = XLColor.FromHtml("#FDE7C9");
        public XLColor GapBorder { get; } = XLColor.FromHtml("#E5494F");
        public XLColor Boundary { get; } = XLColor.FromHtml("#FF9800");
        public XLColor HeaderBg { get; } = XLColor.FromHtml("#263238");
        public XLColor HeaderBg2 { get; } = XLColor.FromHtml("#37474F");
        public XLColor Ink { get; } = XLColor.FromHtml("#263238");
        public XLColor SubInk { get; } = XLColor.FromHtml("#78909C");
        public XLColor Good { get; } = XLColor.FromHtml("#2E7D32");
        public XLColor Mid { get; } = XLColor.FromHtml("#E65100");
        public XLColor Low { get; } = XLColor.FromHtml("#C62828");
    }
}

// ─── 화면 모델 (positional records → camelCase 자동 바인딩). 클라이언트가 그린 현재 상태 그대로. ─────────────

public sealed record CycleExcelModel(
    string FlowName,
    string ChartStart,
    string ChartEnd,
    string? ViewMode,
    string? HeadCallId,
    string? TailCallId,
    string? HeadName,
    string? TailName,
    double? AvgCycleMs,
    double? AvgActiveMs,
    List<CycleExcelLane> Lanes,
    List<string> CycleBoundaries,
    List<string> TailEdges,
    List<CycleExcelGap> TopGaps,
    bool ShowMaxGap,
    int SelectedGapIndex);

public sealed record CycleExcelLane(
    string CallId,
    string CallName,
    string? WorkName,
    int LaneIndex,
    string? InTag,
    string? OutTag,
    List<CycleExcelInterval> Intervals,
    List<CycleExcelInterval> OutIntervals,
    List<CycleExcelInterval> InIntervals);

public sealed record CycleExcelInterval(string Start, string End);

/// <summary>활성 Gap — 좌표는 chartStart 기준 오프셋(ms), tz 무관.</summary>
public sealed record CycleExcelGap(string CallId, double DurMs, double StartOffMs, double EndOffMs);
