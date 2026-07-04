// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Globalization;
using ClosedXML.Excel;

namespace DSPilot.Services;

/// <summary>
/// 종합효율 현황(uptime-oee) 페이지의 Excel(.xlsx) 내보내기.
///
/// 서버가 OEE 를 다시 계산하지 않고, 클라이언트(정적 uptime-oee.html)가 화면에 그린 현재 상태
/// (<see cref="OeeExcelModel"/>: 종합 지표 + 가용성 분해 + 정지 구성 + 설비별 순위 + 정지 이벤트 로그
///  + 일자별 추이 차트 캔버스를 PNG 로 캡처한 이미지)를 그대로 받아 렌더한다 → 화면과 1:1(WYSIWYG).
///   Sheet1 "요약·차트" — 기간/OEE 6지표/가용성 분해/정지 구성 표 + 추이 차트 이미지.
///   Sheet2 "설비별 순위" — 설비(Flow)별 OEE·A·P·Q·정지 테이블.
///   Sheet3 "정지 이벤트" — 정지 이벤트 로그 테이블.
/// CSV(데이터 전용)는 클라이언트가 직접 빌드해 브라우저에서 다운로드한다(서버 미경유).
/// 차트 이미지 DTO(<see cref="TrendImageDto"/>)는 flow-trend 내보내기와 공용.
/// </summary>
public static class OeeExcelExporter
{
    public const string XlsxMimeType = ExcelExporterBase.XlsxMimeType;

    public static byte[] BuildOeeExcel(OeeExcelModel model)
    {
        using var workbook = new XLWorkbook();
        BuildSummarySheet(workbook, model);
        BuildRankingSheet(workbook, model);
        BuildDowntimeSheet(workbook, model);

        return ExcelExporterBase.SaveToBytes(workbook);
    }

    // ── Sheet1: 요약(지표·가용성·정지 구성) + 차트 이미지 ─────────────────────────────
    private static void BuildSummarySheet(XLWorkbook workbook, OeeExcelModel model)
    {
        var ws = workbook.Worksheets.Add("요약·차트");
        var header = XLColor.FromHtml("#263238");
        var subInk = XLColor.FromHtml("#546E7A");
        var k = model.Kpi ?? new OeeKpiDto();

        // 제목
        var titleCell = ws.Cell(1, 1);
        titleCell.Value = $"{(string.IsNullOrWhiteSpace(model.Title) ? "라인 전체" : model.Title)} · 종합효율 현황";
        titleCell.Style.Font.Bold = true;
        titleCell.Style.Font.FontSize = 14;
        ws.Range(1, 1, 1, 3).Merge();
        ws.Row(1).Height = 24;

        var sub = ws.Cell(2, 1);
        var period = $"{FormatWall(model.PeriodStart)} ~ {FormatWall(model.PeriodEnd)}";
        var scope = string.IsNullOrWhiteSpace(model.FlowName) ? "라인 전체 합산" : "설비: " + model.FlowName;
        sub.Value = $"{scope}  ·  기간 {period}";
        sub.Style.Font.FontColor = subInk;
        sub.Style.Font.FontSize = 10;
        ws.Range(2, 1, 2, 3).Merge();

        int r = 4;

        // ── OEE 종합 지표 표 ──
        r = SectionHead(ws, r, header, "OEE 종합 지표", "값");
        var availSrc = k.AvailabilitySource switch
        {
            "cycle" => "사이클", "shift" => "시프트", "auto" => "자동추정", "calendar" => "달력", _ => k.AvailabilitySource ?? "-"
        };
        var qualSrc = k.QualitySource switch { "assumed" => "가정(100%)", "manual" => "사용자", "measured" => "측정값", _ => "-" };
        var indicators = new (string Label, string Value)[]
        {
            ("OEE 종합", FormatPct(k.Oee)),
            ("가용성 A", FormatPct(k.Availability) + "  (" + availSrc + ")"),
            ("성능 P", FormatPct(k.Performance) + "  (14일 평균)"),
            ("품질 Q", FormatPct(k.Quality) + "  (" + qualSrc + ")"),
            ("MTBF", k.FailureCount == 0 ? "무비가동" : FormatMs(k.Mtbf)),
            ("MTTR", k.FailureCount == 0 ? "무비가동" : FormatMs(k.Mttr)),
            ("정지 건수", k.DowntimeCount.ToString("N0", CultureInfo.InvariantCulture) + " 건"),
            ("정지 시간", FormatMs(k.DowntimeMs)),
            ("가동시간 이상치(표준)", FormatMs(k.CtThresholdMs)),
            ("정상 사이클 수", (k.NormalCycleCount ?? 0).ToString("N0", CultureInfo.InvariantCulture) + " 건"),
        };
        foreach (var (label, value) in indicators) r = LabelValueRow(ws, r, label, value);
        r++;

        // ── 가용성 시간 구성 ──
        if (model.AvailComp is { } ac)
        {
            r = SectionHead(ws, r, header, "가용성 시간 구성", "값");
            r = LabelValueRow(ws, r, ac.RunLabel, $"{FormatMs(ac.RunMs)}  ({FormatOne(ac.RunPct)}%)");
            r = LabelValueRow(ws, r, ac.StopLabel, $"{FormatMs(ac.StopMs)}  ({FormatOne(ac.StopPct)}%)");
            r++;
        }

        // ── 정지 구성(고장/유지보수) ──
        if (model.FaultSegs is { Count: > 0 })
        {
            r = SectionHead(ws, r, header, "정지 구성", "값");
            foreach (var seg in model.FaultSegs)
                r = LabelValueRow(ws, r, seg.Label, $"{FormatMs(seg.Ms)}  ({seg.Share}%)");
        }

        ws.Column(1).Width = 22;
        ws.Column(2).Width = 26;

        // 차트 이미지 — 표 오른쪽(D열~)에 세로로 쌓는다.
        int imgRow = 4;
        const int imgCol = 4;
        foreach (var img in model.Images ?? new List<TrendImageDto>())
        {
            var bytes = DecodeDataUrl(img.DataUrl);
            if (bytes is null || bytes.Length == 0) continue;

            var caption = ws.Cell(imgRow, imgCol);
            caption.Value = img.Name;
            caption.Style.Font.Bold = true;
            caption.Style.Font.FontSize = 11;
            imgRow++;

            int w = img.Width > 0 ? img.Width : 640;
            int h = img.Height > 0 ? img.Height : 260;
            if (w > 720) { h = (int)Math.Round(h * (720.0 / w)); w = 720; }

            try
            {
                using var imgStream = new MemoryStream(bytes);
                ws.AddPicture(imgStream, img.Name)
                    .MoveTo(ws.Cell(imgRow, imgCol))
                    .WithSize(w, h);
            }
            catch
            {
                // 이미지 디코딩/삽입 실패는 무시(데이터 시트는 정상 생성).
            }
            imgRow += (int)Math.Ceiling(h / 20.0) + 2;
        }

        ws.SheetView.FreezeRows(3);
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.Footer.Center.AddText($"Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    }

    // ── Sheet2: 설비별 OEE 순위 ─────────────────────────────────────────────────────
    private static void BuildRankingSheet(XLWorkbook workbook, OeeExcelModel model)
    {
        var ws = workbook.Worksheets.Add("설비별 순위");
        var header = XLColor.FromHtml("#37474F");

        var headers = new[] { "순위", "설비(Flow)", "OEE(%)", "가용성(%)", "성능(%)", "품질(%)", "정지건수", "정지시간", "생산수" };
        for (int i = 0; i < headers.Length; i++)
        {
            var c = ws.Cell(1, i + 1);
            c.Value = headers[i];
            c.Style.Font.Bold = true;
            c.Style.Fill.BackgroundColor = header;
            c.Style.Font.FontColor = XLColor.White;
        }

        int row = 2;
        int rank = 1;
        foreach (var rk in (model.Ranking ?? new List<OeeRankRowDto>()))
        {
            ws.Cell(row, 1).Value = rank++;
            ws.Cell(row, 2).Value = rk.FlowName;
            SetPct(ws.Cell(row, 3), rk.Oee);
            SetPct(ws.Cell(row, 4), rk.Availability);
            SetPct(ws.Cell(row, 5), rk.Performance);
            SetPct(ws.Cell(row, 6), rk.Quality);
            ws.Cell(row, 7).Value = rk.DowntimeCount;
            ws.Cell(row, 8).Value = FormatMs(rk.DowntimeMs);
            ws.Cell(row, 9).Value = rk.TotalCount ?? 0;
            row++;
        }

        // 고정 너비 — ClosedXML AdjustToContents 한글 폭 버그 회피(메모리 규칙).
        ws.Column(1).Width = 6;
        ws.Column(2).Width = 26;
        ws.Column(3).Width = 10;
        ws.Column(4).Width = 11;
        ws.Column(5).Width = 10;
        ws.Column(6).Width = 10;
        ws.Column(7).Width = 10;
        ws.Column(8).Width = 12;
        ws.Column(9).Width = 10;
        ws.SheetView.FreezeRows(1);
    }

    // ── Sheet3: 정지 이벤트 로그 ─────────────────────────────────────────────────────
    private static void BuildDowntimeSheet(XLWorkbook workbook, OeeExcelModel model)
    {
        var ws = workbook.Worksheets.Add("정지 이벤트");
        var header = XLColor.FromHtml("#37474F");

        var headers = new[] { "발생", "복구", "지속", "설비(Flow)", "장치", "구분", "감지", "상태" };
        for (int i = 0; i < headers.Length; i++)
        {
            var c = ws.Cell(1, i + 1);
            c.Value = headers[i];
            c.Style.Font.Bold = true;
            c.Style.Fill.BackgroundColor = header;
            c.Style.Font.FontColor = XLColor.White;
        }

        int row = 2;
        foreach (var d in (model.Downtime ?? new List<OeeDowntimeRowDto>()))
        {
            ws.Cell(row, 1).Value = FormatWall(d.StartAt);
            ws.Cell(row, 2).Value = string.IsNullOrWhiteSpace(d.EndAt) ? "-" : FormatWall(d.EndAt);
            ws.Cell(row, 3).Value = FormatMs(d.DurationMs);
            ws.Cell(row, 4).Value = d.FlowName ?? "-";
            ws.Cell(row, 5).Value = d.DeviceName ?? "-";
            ws.Cell(row, 6).Value = d.IsFailure ? "고장" : "유지보수";
            ws.Cell(row, 7).Value = DetectLabel(d.DetectSource);
            ws.Cell(row, 8).Value = d.Status == "open" ? "진행중" : "복구";
            row++;
        }

        ws.Column(1).Width = 18;
        ws.Column(2).Width = 18;
        ws.Column(3).Width = 10;
        ws.Column(4).Width = 22;
        ws.Column(5).Width = 18;
        ws.Column(6).Width = 10;
        ws.Column(7).Width = 10;
        ws.Column(8).Width = 8;
        ws.SheetView.FreezeRows(1);
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────

    private static int SectionHead(IXLWorksheet ws, int r, XLColor bg, string left, string right)
    {
        var head = ws.Range(r, 1, r, 2);
        ws.Cell(r, 1).Value = left;
        ws.Cell(r, 2).Value = right;
        head.Style.Font.Bold = true;
        head.Style.Fill.BackgroundColor = bg;
        head.Style.Font.FontColor = XLColor.White;
        return r + 1;
    }

    private static int LabelValueRow(IXLWorksheet ws, int r, string label, string value)
    {
        ws.Cell(r, 1).Value = label;
        ws.Cell(r, 1).Style.Font.Bold = true;
        ws.Cell(r, 2).Value = value;
        return r + 1;
    }

    private static void SetPct(IXLCell cell, double? v) => cell.Value = v is null ? "산출불가" : (v.Value * 100).ToString("0.0", CultureInfo.InvariantCulture);

    private static string DetectLabel(string? s) => s switch
    {
        "nocycle" => "무사이클",
        "fault-bit" => "고장비트",
        "usertag" => "고장비트",
        "manual" => "수동",
        _ => string.IsNullOrWhiteSpace(s) ? "-" : s!
    };

    private static byte[]? DecodeDataUrl(string? dataUrl)
    {
        if (string.IsNullOrEmpty(dataUrl)) return null;
        var idx = dataUrl.IndexOf(',');
        var b64 = idx >= 0 ? dataUrl[(idx + 1)..] : dataUrl;
        try { return Convert.FromBase64String(b64); }
        catch { return null; }
    }

    /// <summary>ISO/로컬 문자열의 벽시계 표기(yyyy-MM-dd HH:mm:ss). 파싱 실패 시 원본 그대로.</summary>
    private static string FormatWall(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "-";
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return s;
    }

    private static string FormatPct(double? v) => v is null ? "—" : (v.Value * 100).ToString("0.0", CultureInfo.InvariantCulture) + " %";

    private static string FormatOne(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

    private static string FormatMs(double? ms)
    {
        if (ms is null || ms <= 0) return "-";
        var v = ms.Value;
        if (v < 1000) return $"{Math.Round(v)} ms";
        if (v < 60000) return (v / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " 초";
        if (v < 3600000) return (v / 60000.0).ToString("0.0", CultureInfo.InvariantCulture) + " 분";
        if (v < 86400000) return (v / 3600000.0).ToString("0.0", CultureInfo.InvariantCulture) + " 시간";
        return (v / 86400000.0).ToString("0.0", CultureInfo.InvariantCulture) + " 일";
    }
}

// ─── 화면 모델 (positional/init records → camelCase 자동 바인딩). 클라이언트가 그린 현재 상태 그대로. ─────

public sealed record OeeExcelModel(
    string? Title,
    string? SystemName,
    string? FlowName,
    string PeriodStart,
    string PeriodEnd,
    OeeKpiDto Kpi,
    OeeAvailCompDto? AvailComp,
    List<OeeFaultSegDto> FaultSegs,
    List<OeeRankRowDto> Ranking,
    List<OeeDowntimeRowDto> Downtime,
    List<TrendImageDto> Images);

/// <summary>OEE 종합 6지표 + 부가 수치. 비율은 0~1, 시간은 ms.</summary>
public sealed record OeeKpiDto
{
    public double? Oee { get; init; }
    public double? Availability { get; init; }
    public double? Performance { get; init; }
    public double? Quality { get; init; }
    public double? Mtbf { get; init; }
    public double? Mttr { get; init; }
    public string? AvailabilitySource { get; init; }
    public string? QualitySource { get; init; }
    public int DowntimeCount { get; init; }
    public double DowntimeMs { get; init; }
    public double? CtThresholdMs { get; init; }
    public int? NormalCycleCount { get; init; }
    public int FailureCount { get; init; }
    public int? GoodCount { get; init; }
    public int? TotalCount { get; init; }
}

/// <summary>가용성 시간 구성(활성 모드 분해). 비율은 %(0~100), 시간은 ms.</summary>
public sealed record OeeAvailCompDto(
    string? Mode,
    string RunLabel,
    double RunMs,
    double RunPct,
    string StopLabel,
    double StopMs,
    double StopPct);

/// <summary>정지 구성 도넛 세그먼트(고장/유지보수). Ms=지속 합, Share=비율(%).</summary>
public sealed record OeeFaultSegDto(string Label, double Ms, int Share);

/// <summary>설비별 OEE 순위 한 행. 비율은 0~1, 시간은 ms.</summary>
public sealed record OeeRankRowDto(
    string FlowName,
    double? Oee,
    double? Availability,
    double? Performance,
    double? Quality,
    int DowntimeCount,
    double DowntimeMs,
    int? TotalCount);

/// <summary>정지 이벤트 로그 한 행. 시각=로컬 ISO 문자열, DurationMs=ms.</summary>
public sealed record OeeDowntimeRowDto(
    string? StartAt,
    string? EndAt,
    double? DurationMs,
    string? FlowName,
    string? DeviceName,
    bool IsFailure,
    string? DetectSource,
    string? Status);
