// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using ClosedXML.Excel;
using DSPilot.Models.UserTagAlerts;

namespace DSPilot.Services;

/// <summary>
/// 이상·알람(UserTag/Abnormal) 목록의 Excel(.xlsx) 내보내기.
/// /api/user-tags/excel 이 현재 필터(기간·검색·System·구분·설비)로 조회한 알림 행을 단일 시트 테이블로 렌더한다.
/// 컬럼은 CSV 내보내기와 동일 데이터원(UserTagAlertRecord)을 쓰되 한글 헤더로 표기한다.
/// </summary>
public static class UserTagAlertExcelExporter
{
    public const string XlsxMimeType = ExcelExporterBase.XlsxMimeType;

    public static byte[] Build(IReadOnlyList<UserTagAlertRecord> rows, DateTime periodStartLocal, DateTime periodEndLocal, string? flow)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("알람");
        const int lastCol = 9;

        var titleText = "이상·알람 조회" + (string.IsNullOrWhiteSpace(flow) ? "" : $" · 설비 {flow} (자동감지만)");
        ExcelExporterBase.ApplyTitleRow(ws, 1, titleText, lastCol, 22);
        ExcelExporterBase.ApplySubtitleRow(ws, 2,
            $"기간 {periodStartLocal:yyyy-MM-dd HH:mm} ~ {periodEndLocal:yyyy-MM-dd HH:mm}  ·  {rows.Count:N0} 건",
            lastCol);

        const int headerRow = 4;
        ExcelExporterBase.ApplyHeaderRow(ws, headerRow,
            ["시각", "레벨", "구분", "System", "이름", "경로(주소)", "조건", "매칭값", "실제값"]);

        int row = headerRow + 1;
        foreach (var a in rows)
        {
            ws.Cell(row, 1).Value = a.OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            ws.Cell(row, 2).Value = a.LogLevel;
            ws.Cell(row, 3).Value = string.Equals(a.ValueType, "Abnormal", StringComparison.Ordinal) ? "자동감지" : "수동등록TAG";
            ws.Cell(row, 4).Value = a.SystemName;
            ws.Cell(row, 5).Value = a.Name;
            ws.Cell(row, 6).Value = a.TagAddress;
            ws.Cell(row, 7).Value = a.MatchOp;
            ws.Cell(row, 8).Value = a.MatchValue ?? "";
            ws.Cell(row, 9).Value = a.ActualValue;
            row++;
        }

        // 고정 너비 — ClosedXML AdjustToContents 의 한글 폭 버그 회피(메모리 규칙).
        ws.Column(1).Width = 20;
        ws.Column(2).Width = 8;
        ws.Column(3).Width = 10;
        ws.Column(4).Width = 16;
        ws.Column(5).Width = 24;
        ws.Column(6).Width = 28;
        ws.Column(7).Width = 16;
        ws.Column(8).Width = 14;
        ws.Column(9).Width = 16;
        ExcelExporterBase.FreezeAndFooter(ws, headerRow);

        return ExcelExporterBase.SaveToBytes(workbook);
    }
}
