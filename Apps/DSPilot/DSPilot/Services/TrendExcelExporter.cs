// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Globalization;
using ClosedXML.Excel;

namespace DSPilot.Services;

/// <summary>
/// 기간별 추이(flow-trend) 페이지의 Excel(.xlsx) 내보내기.
///
/// 서버가 추이를 다시 집계하지 않고, 클라이언트(정적 flow-trend.html)가 화면에 그린 현재 상태
/// (<see cref="TrendExcelModel"/>: 요약 통계 + 버킷별 집계 + 화면 차트 캔버스를 PNG 로 캡처한 이미지)를
/// 그대로 받아 렌더한다 → 화면과 1:1(WYSIWYG).
///   Sheet1 "요약·차트" — 기간/요약 통계 표 + 화면 차트(가동시간 스택 막대·가동횟수 라인) 이미지.
///   Sheet2 "데이터"     — 버킷별 집계 데이터 테이블(CSV 와 동일 컬럼).
/// CSV(데이터 전용)는 클라이언트가 buckets 로 직접 빌드해 브라우저에서 다운로드한다(서버 미경유).
/// </summary>
public static class TrendExcelExporter
{
    public const string XlsxMimeType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static byte[] BuildTrendExcel(TrendExcelModel model)
    {
        using var workbook = new XLWorkbook();
        BuildSummarySheet(workbook, model);
        BuildDataSheet(workbook, model);

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }

    // ── Sheet1: 요약 + 차트 이미지 ─────────────────────────────────────────────────
    private static void BuildSummarySheet(XLWorkbook workbook, TrendExcelModel model)
    {
        var ws = workbook.Worksheets.Add("요약·차트");
        var header = XLColor.FromHtml("#263238");
        var subInk = XLColor.FromHtml("#546E7A");

        // 제목
        var titleCell = ws.Cell(1, 1);
        titleCell.Value = $"{model.Title} · 기간별 추이";
        titleCell.Style.Font.Bold = true;
        titleCell.Style.Font.FontSize = 14;
        ws.Range(1, 1, 1, 4).Merge();
        ws.Row(1).Height = 24;

        var sub = ws.Cell(2, 1);
        var period = $"{FormatWall(model.PeriodStart)} ~ {FormatWall(model.PeriodEnd)}";
        var gran = model.Granularity switch { "hour" => "1시간", "day" => "1일", "week" => "1주", _ => model.Granularity ?? "-" };
        sub.Value = $"{(string.IsNullOrEmpty(model.SystemName) ? "" : model.SystemName + "  ·  ")}기간 {period}  ·  버킷 {gran}";
        sub.Style.Font.FontColor = subInk;
        sub.Style.Font.FontSize = 10;
        ws.Range(2, 1, 2, 4).Merge();

        // 요약 통계 표 (라벨/값 2열)
        var s = model.Stats ?? new TrendStatsDto(0, 0, null, null, null, null, null, 0, 0, 0);
        var rows = new (string Label, string Value)[]
        {
            ("사이클 수", s.CycleCount.ToString("N0", CultureInfo.InvariantCulture) + " 건"),
            ("비가동 사이클", s.IdleCount.ToString("N0", CultureInfo.InvariantCulture) + " 건"),
            ("평균 가동시간(CT)", FormatMs(s.AvgCT)),
            ("평균 동작시간(MT)", FormatMs(s.AvgMT)),
            ("평균 대기시간(WT)", FormatMs(s.AvgWT)),
            ("최소 CT", FormatMs(s.MinCT)),
            ("최대 CT", FormatMs(s.MaxCT)),
            ("가동률", (s.Utilization * 100).ToString("0.0", CultureInfo.InvariantCulture) + " %"),
            ("총 동작시간", FormatMs(s.TotalMt)),
            ("총 대기시간", FormatMs(s.TotalWt)),
        };

        int r = 4;
        var head = ws.Range(r, 1, r, 2);
        ws.Cell(r, 1).Value = "요약";
        ws.Cell(r, 2).Value = "값";
        head.Style.Font.Bold = true;
        head.Style.Fill.BackgroundColor = header;
        head.Style.Font.FontColor = XLColor.White;
        r++;
        foreach (var (label, value) in rows)
        {
            ws.Cell(r, 1).Value = label;
            ws.Cell(r, 1).Style.Font.Bold = true;
            ws.Cell(r, 2).Value = value;
            r++;
        }
        ws.Column(1).Width = 20;
        ws.Column(2).Width = 22;

        // 차트 이미지 — 요약 표 오른쪽(D열~)에 세로로 쌓는다.
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
            // 과도하게 큰 캡처는 폭 720px 로 제한(비율 유지).
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
            // 이미지 높이만큼 행을 비워 다음 이미지와 겹치지 않게 한다(기본 행 높이 ≈ 20px).
            imgRow += (int)Math.Ceiling(h / 20.0) + 2;
        }

        ws.SheetView.FreezeRows(3);
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.Footer.Center.AddText($"Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    }

    // ── Sheet2: 버킷별 데이터 테이블 ─────────────────────────────────────────────────
    private static void BuildDataSheet(XLWorkbook workbook, TrendExcelModel model)
    {
        var ws = workbook.Worksheets.Add("데이터");
        var header = XLColor.FromHtml("#37474F");

        var headers = new[] { "버킷시각", "사이클수", "비가동수", "평균 CT(초)", "평균 동작(초)", "평균 대기(초)" };
        for (int i = 0; i < headers.Length; i++)
        {
            var c = ws.Cell(1, i + 1);
            c.Value = headers[i];
            c.Style.Font.Bold = true;
            c.Style.Fill.BackgroundColor = header;
            c.Style.Font.FontColor = XLColor.White;
        }

        int row = 2;
        foreach (var b in (model.Buckets ?? new List<TrendBucketDto>()))
        {
            ws.Cell(row, 1).Value = FormatWall(b.Ts);
            ws.Cell(row, 2).Value = b.Count;
            ws.Cell(row, 3).Value = b.Idle;
            ws.Cell(row, 4).Value = Math.Round(b.AvgCT / 1000.0, 2);
            ws.Cell(row, 5).Value = Math.Round(b.AvgMT / 1000.0, 2);
            ws.Cell(row, 6).Value = Math.Round(b.AvgWT / 1000.0, 2);
            row++;
        }

        // 고정 너비 — ClosedXML AdjustToContents 의 한글 폭 버그 회피(메모리 규칙).
        ws.Column(1).Width = 18;
        ws.Column(2).Width = 10;
        ws.Column(3).Width = 10;
        ws.Column(4).Width = 12;
        ws.Column(5).Width = 12;
        ws.Column(6).Width = 12;
        ws.SheetView.FreezeRows(1);
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────

    private static byte[]? DecodeDataUrl(string? dataUrl)
    {
        if (string.IsNullOrEmpty(dataUrl)) return null;
        var idx = dataUrl.IndexOf(',');
        var b64 = idx >= 0 ? dataUrl[(idx + 1)..] : dataUrl;
        try { return Convert.FromBase64String(b64); }
        catch { return null; }
    }

    /// <summary>ISO/로컬 문자열의 벽시계 표기(yyyy-MM-dd HH:mm). 파싱 실패 시 원본 그대로.</summary>
    private static string FormatWall(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "-";
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        return s;
    }

    private static string FormatMs(double? ms)
    {
        if (ms is null || ms <= 0) return "-";
        var v = ms.Value;
        if (v < 1000) return $"{Math.Round(v)} ms";
        if (v < 60000) return (v / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + " 초";
        if (v < 3600000) return (v / 60000.0).ToString("0.00", CultureInfo.InvariantCulture) + " 분";
        return (v / 3600000.0).ToString("0.00", CultureInfo.InvariantCulture) + " 시간";
    }
}

// ─── 화면 모델 (positional records → camelCase 자동 바인딩). 클라이언트가 그린 현재 상태 그대로. ─────────────

public sealed record TrendExcelModel(
    string Title,
    string? SystemName,
    string PeriodStart,
    string PeriodEnd,
    string? Granularity,
    TrendStatsDto Stats,
    List<TrendBucketDto> Buckets,
    List<TrendImageDto> Images);

/// <summary>요약 통계 — 시간값은 모두 ms.</summary>
public sealed record TrendStatsDto(
    int CycleCount,
    int IdleCount,
    double? AvgCT,
    double? AvgMT,
    double? AvgWT,
    double? MinCT,
    double? MaxCT,
    double Utilization,
    double TotalMt,
    double TotalWt);

/// <summary>버킷별 집계 — Ts=버킷 시작(로컬 ISO), 나머지 시간값은 ms.</summary>
public sealed record TrendBucketDto(
    string Ts,
    int Count,
    int Idle,
    double AvgCT,
    double AvgMT,
    double AvgWT);

/// <summary>화면 차트 캔버스 캡처. DataUrl = "data:image/png;base64,...". Width/Height = 표시 픽셀.</summary>
public sealed record TrendImageDto(
    string Name,
    string DataUrl,
    int Width,
    int Height);
