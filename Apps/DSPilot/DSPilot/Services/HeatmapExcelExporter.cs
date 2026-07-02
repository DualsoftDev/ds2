// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Globalization;
using ClosedXML.Excel;

namespace DSPilot.Services;

/// <summary>
/// 동작편차(heatmap) 페이지의 Excel(.xlsx) 내보내기.
///
/// 서버가 통계를 다시 집계하지 않고, 클라이언트(정적 heatmap.html)가 화면에 표시 중인 현재 데이터
/// (<see cref="HeatmapExcelModel"/>: Flow/Call 별 평균·표준편차·변동계수·실행횟수 + tier 임계)를
/// 그대로 받아 한 장의 데이터 테이블로 렌더한다 → 화면(테이블 보기)과 1:1(WYSIWYG).
///   Sheet "동작편차" — Flow / Call / Work / 평균 / 표준편차 / 변동계수(CV) / 편차(%) / 등급 / 실행횟수.
///     등급(정상/주의/위험)은 CV 임계(cautionCv/dangerCv)로 서버에서 파생하고 셀 배경색으로 시각화.
/// CSV(데이터 전용)는 클라이언트가 rows 로 직접 빌드해 다운로드한다(서버 미경유).
/// </summary>
public static class HeatmapExcelExporter
{
    public const string XlsxMimeType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    // tier 색(범례 3색과 동조 — 연한 배경). heatmap.html .hm-tier-bg-* 와 톤 일치.
    private static readonly XLColor NormalBg = XLColor.FromHtml("#E4F4EA");
    private static readonly XLColor CautionBg = XLColor.FromHtml("#FCEFCF");
    private static readonly XLColor DangerBg = XLColor.FromHtml("#FADBD7");

    public static byte[] BuildHeatmapExcel(HeatmapExcelModel model)
    {
        using var workbook = new XLWorkbook();
        BuildDataSheet(workbook, model);

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }

    private static void BuildDataSheet(XLWorkbook workbook, HeatmapExcelModel model)
    {
        var ws = workbook.Worksheets.Add("동작편차");
        var header = XLColor.FromHtml("#37474F");

        // 제목 / 부제
        var titleCell = ws.Cell(1, 1);
        titleCell.Value = $"{(string.IsNullOrWhiteSpace(model.Title) ? "전체" : model.Title)} · 동작편차";
        titleCell.Style.Font.Bold = true;
        titleCell.Style.Font.FontSize = 14;
        ws.Range(1, 1, 1, 9).Merge();
        ws.Row(1).Height = 24;

        var caution = model.CautionCv > 0 ? model.CautionCv : 0.10;
        var danger = model.DangerCv > 0 ? model.DangerCv : 0.30;
        var sub = ws.Cell(2, 1);
        sub.Value = $"편차는 평균 대비(CV=표준편차/평균) · 정상 < {Pct(caution)}% ≤ 주의 < {Pct(danger)}% ≤ 위험";
        sub.Style.Font.FontColor = XLColor.FromHtml("#546E7A");
        sub.Style.Font.FontSize = 10;
        ws.Range(2, 1, 2, 9).Merge();

        // 헤더 행
        const int headRow = 4;
        var headers = new[] { "Flow", "Call", "Work", "평균(ms)", "표준편차(ms)", "변동계수(CV)", "편차(±%)", "등급", "실행횟수(N)" };
        for (int i = 0; i < headers.Length; i++)
        {
            var c = ws.Cell(headRow, i + 1);
            c.Value = headers[i];
            c.Style.Font.Bold = true;
            c.Style.Fill.BackgroundColor = header;
            c.Style.Font.FontColor = XLColor.White;
        }

        int row = headRow + 1;
        foreach (var r in (model.Rows ?? new List<HeatmapExcelRow>()))
        {
            var (tierLabel, tierBg) = ClassifyTier(r.Cv, caution, danger);
            ws.Cell(row, 1).Value = r.FlowName ?? "";
            ws.Cell(row, 2).Value = r.CallName ?? "";
            ws.Cell(row, 3).Value = r.WorkName ?? "";
            ws.Cell(row, 4).Value = Math.Round(r.AverageMs);
            ws.Cell(row, 5).Value = Math.Round(r.StdDevMs);
            ws.Cell(row, 6).Value = Math.Round(r.Cv, 2);
            ws.Cell(row, 7).Value = Math.Round(r.Cv * 100, 1);
            ws.Cell(row, 8).Value = tierLabel;
            ws.Cell(row, 9).Value = r.GoingCount;

            // 편차/등급 셀 배경 = tier 색(위험이 즉시 보이도록).
            ws.Cell(row, 6).Style.Fill.BackgroundColor = tierBg;
            ws.Cell(row, 7).Style.Fill.BackgroundColor = tierBg;
            ws.Cell(row, 8).Style.Fill.BackgroundColor = tierBg;
            row++;
        }

        // 고정 너비 — ClosedXML AdjustToContents 의 한글 폭 버그 회피(메모리 규칙).
        ws.Column(1).Width = 22;
        ws.Column(2).Width = 24;
        ws.Column(3).Width = 20;
        ws.Column(4).Width = 12;
        ws.Column(5).Width = 13;
        ws.Column(6).Width = 13;
        ws.Column(7).Width = 11;
        ws.Column(8).Width = 9;
        ws.Column(9).Width = 12;
        ws.SheetView.FreezeRows(headRow);
        ws.PageSetup.Footer.Center.AddText($"Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    }

    private static (string Label, XLColor Bg) ClassifyTier(double cv, double caution, double danger)
    {
        if (cv < caution) return ("정상", NormalBg);
        if (cv < danger) return ("주의", CautionBg);
        return ("위험", DangerBg);
    }

    private static string Pct(double cv) => Math.Round(cv * 100).ToString(CultureInfo.InvariantCulture);
}

// ─── 화면 모델 (positional records → camelCase 자동 바인딩). 클라이언트가 표시 중인 현재 데이터 그대로. ─────────────

public sealed record HeatmapExcelModel(
    string Title,
    double CautionCv,
    double DangerCv,
    List<HeatmapExcelRow> Rows);

/// <summary>Call 별 동작편차 행 — 시간값은 모두 ms, Cv=σ/평균.</summary>
public sealed record HeatmapExcelRow(
    string FlowName,
    string CallName,
    string? WorkName,
    double AverageMs,
    double StdDevMs,
    double Cv,
    long GoingCount);
