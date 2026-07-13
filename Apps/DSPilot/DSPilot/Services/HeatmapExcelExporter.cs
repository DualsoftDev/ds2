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
    public const string XlsxMimeType = ExcelExporterBase.XlsxMimeType;

    // tier 색(범례와 동조 — 연한 배경). heatmap.html .hm-tier-bg-* 와 톤 일치. steady=변동 공정(정보색).
    private static readonly XLColor NormalBg = XLColor.FromHtml("#E4F4EA");
    private static readonly XLColor SteadyBg = XLColor.FromHtml("#E2EFF8");
    private static readonly XLColor CautionBg = XLColor.FromHtml("#FCEFCF");
    private static readonly XLColor DangerBg = XLColor.FromHtml("#FADBD7");

    public static byte[] BuildHeatmapExcel(HeatmapExcelModel model)
    {
        using var workbook = new XLWorkbook();
        BuildDataSheet(workbook, model);

        return ExcelExporterBase.SaveToBytes(workbook);
    }

    private static void BuildDataSheet(XLWorkbook workbook, HeatmapExcelModel model)
    {
        var ws = workbook.Worksheets.Add("동작편차");
        const int lastCol = 12;

        ExcelExporterBase.ApplyTitleRow(ws, 1,
            $"{(string.IsNullOrWhiteSpace(model.Title) ? "전체" : model.Title)} · 동작편차", lastCol, 24);

        var caution = model.CautionCv > 0 ? model.CautionCv : 0.10;
        var danger = model.DangerCv > 0 ? model.DangerCv : 0.30;
        ExcelExporterBase.ApplySubtitleRow(ws, 2,
            $"편차 = 중앙값 대비 로버스트 편차(대기·개입 표본 영향 최소화) · 등급 = 최근 창이 평소 대비 악화됐는지 " +
            $"(안정 < {Pct(caution)}% · 변동 공정 = 평소 수준 · 주의/위험 = 악화) · 지연 = 중앙값×3 초과 표본 수", lastCol);

        // 헤더 행
        const int headRow = 4;
        ExcelExporterBase.ApplyHeaderRow(ws, headRow,
            ["Flow", "Call", "Work", "대표시간·중앙값(ms)", "정상범위(ms)", "편차(±%)", "등급", "지연(회)",
             "평균(ms)·참고", "표준편차(ms)·참고", "CV·참고", "실행횟수(N)"]);

        int row = headRow + 1;
        foreach (var r in (model.Rows ?? new List<HeatmapExcelRow>()))
        {
            // 등급은 클라이언트 판정(평소 대비 악화 — 서버는 baseline 을 모름)을 우선, 구 클라이언트면 CV 절대 임계 폴백.
            var (tierLabel, tierBg) = string.IsNullOrEmpty(r.Tier)
                ? ClassifyTierByCv(r.Cv, caution, danger)
                : (r.Tier!, TierBg(r.Tier!));
            var devCv = r.RobustCv ?? r.Cv;

            ws.Cell(row, 1).Value = r.FlowName ?? "";
            ws.Cell(row, 2).Value = r.CallName ?? "";
            ws.Cell(row, 3).Value = r.WorkName ?? "";
            if (r.MedianMs is { } med) ws.Cell(row, 4).Value = Math.Round(med);
            if (r.P10Ms is { } p10 && r.P90Ms is { } p90)
                ws.Cell(row, 5).Value = $"{Math.Round(p10)} ~ {Math.Round(p90)}";
            ws.Cell(row, 6).Value = Math.Round(devCv * 100, 1);
            ws.Cell(row, 7).Value = tierLabel;
            ws.Cell(row, 8).Value = r.DelayCount;
            ws.Cell(row, 9).Value = Math.Round(r.AverageMs);
            ws.Cell(row, 10).Value = Math.Round(r.StdDevMs);
            ws.Cell(row, 11).Value = Math.Round(r.Cv, 2);
            ws.Cell(row, 12).Value = r.GoingCount;

            // 편차/등급 셀 배경 = tier 색(위험이 즉시 보이도록).
            ws.Cell(row, 6).Style.Fill.BackgroundColor = tierBg;
            ws.Cell(row, 7).Style.Fill.BackgroundColor = tierBg;
            row++;
        }

        // 고정 너비 — ClosedXML AdjustToContents 의 한글 폭 버그 회피(메모리 규칙).
        ws.Column(1).Width = 22;
        ws.Column(2).Width = 24;
        ws.Column(3).Width = 20;
        ws.Column(4).Width = 17;
        ws.Column(5).Width = 16;
        ws.Column(6).Width = 11;
        ws.Column(7).Width = 11;
        ws.Column(8).Width = 9;
        ws.Column(9).Width = 13;
        ws.Column(10).Width = 15;
        ws.Column(11).Width = 10;
        ws.Column(12).Width = 12;
        ExcelExporterBase.FreezeAndFooter(ws, headRow);
    }

    private static (string Label, XLColor Bg) ClassifyTierByCv(double cv, double caution, double danger)
    {
        if (cv < caution) return ("정상", NormalBg);
        if (cv < danger) return ("주의", CautionBg);
        return ("위험", DangerBg);
    }

    private static XLColor TierBg(string label) => label switch
    {
        "위험" => DangerBg,
        "주의" => CautionBg,
        "변동 공정" => SteadyBg,
        _ => NormalBg,
    };

    private static string Pct(double cv) => Math.Round(cv * 100).ToString(CultureInfo.InvariantCulture);
}

// ─── 화면 모델 (positional records → camelCase 자동 바인딩). 클라이언트가 표시 중인 현재 데이터 그대로. ─────────────

public sealed record HeatmapExcelModel(
    string Title,
    double CautionCv,
    double DangerCv,
    List<HeatmapExcelRow> Rows);

/// <summary>
/// Call 별 동작편차 행 — 시간값은 모두 ms, Cv=σ/평균.
/// 로버스트 필드(MedianMs~Tier)는 신 클라이언트가 화면 표시값 그대로 보낸다(WYSIWYG).
/// 구 클라이언트(필드 없음)는 기본값으로 바인딩돼 평균/CV 열만 채워진다.
/// </summary>
public sealed record HeatmapExcelRow(
    string FlowName,
    string CallName,
    string? WorkName,
    double AverageMs,
    double StdDevMs,
    double Cv,
    long GoingCount,
    double? MedianMs = null,
    double? P10Ms = null,
    double? P90Ms = null,
    double? RobustCv = null,
    int DelayCount = 0,
    string? Tier = null);
