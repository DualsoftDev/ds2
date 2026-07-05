// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using ClosedXML.Excel;

namespace DSPilot.Services;

/// <summary>
/// ClosedXML Excel 내보내기 공통 상수 + 헬퍼. 5개 Exporter 가 공유.
/// </summary>
public static class ExcelExporterBase
{
    public const string XlsxMimeType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>워크북을 MemoryStream 에 저장해 byte[] 로 반환한다.</summary>
    public static byte[] SaveToBytes(XLWorkbook workbook)
    {
        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>Row 1 제목 행: Bold 14pt, 병합(1..lastCol), 높이.</summary>
    public static void ApplyTitleRow(IXLWorksheet ws, int row, string text, int lastCol, double height = 22)
    {
        var c = ws.Cell(row, 1);
        c.Value = text;
        c.Style.Font.Bold = true;
        c.Style.Font.FontSize = 14;
        ws.Range(row, 1, row, lastCol).Merge();
        ws.Row(row).Height = height;
    }

    /// <summary>Row 2 부제 행: 회색(#546E7A) 10pt, 병합(1..lastCol).</summary>
    public static void ApplySubtitleRow(IXLWorksheet ws, int row, string text, int lastCol)
    {
        var c = ws.Cell(row, 1);
        c.Value = text;
        c.Style.Font.FontColor = XLColor.FromHtml("#546E7A");
        c.Style.Font.FontSize = 10;
        ws.Range(row, 1, row, lastCol).Merge();
    }

    /// <summary>헤더 행: 배경(bgHtml) + White Bold 텍스트. 기본 배경 #37474F.</summary>
    public static void ApplyHeaderRow(IXLWorksheet ws, int row, string[] headers, string bgHtml = "#37474F")
    {
        var bg = XLColor.FromHtml(bgHtml);
        for (int i = 0; i < headers.Length; i++)
        {
            var c = ws.Cell(row, i + 1);
            c.Value = headers[i];
            c.Style.Font.Bold = true;
            c.Style.Fill.BackgroundColor = bg;
            c.Style.Font.FontColor = XLColor.White;
        }
    }

    /// <summary>헤더 행 고정 + 내보내기 시각 footer.</summary>
    public static void FreezeAndFooter(IXLWorksheet ws, int freezeAtRow)
    {
        ws.SheetView.FreezeRows(freezeAtRow);
        ws.PageSetup.Footer.Center.AddText($"Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    }
}
