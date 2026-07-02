// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Services;
using Microsoft.AspNetCore.Mvc;

namespace DSPilot.Controllers;

/// <summary>
/// 격리형 호스팅용 기간별 추이(flow-trend) 내보내기 API.
/// 추이 자체는 클라이언트가 대시보드 히스토리로 집계하므로 조회 엔드포인트는 없고,
/// Excel(차트+데이터) 내보내기만 서버가 담당한다(차트 이미지는 클라이언트가 캔버스로 캡처해 전달 → WYSIWYG).
/// CSV(데이터 전용)는 클라이언트가 buckets 로 직접 빌드해 다운로드한다(서버 미경유).
/// </summary>
[ApiController]
[Route("api/flow-trend")]
public class FlowTrendController : ControllerBase
{
    /// <summary>
    /// Excel(.xlsx) 내보내기 — WYSIWYG. 클라이언트가 화면에 그린 요약 통계 + 버킷 집계 + 차트 캔버스 캡처
    /// (<see cref="TrendExcelModel"/>) 를 그대로 받아 <see cref="TrendExcelExporter.BuildTrendExcel"/> 로 렌더.
    /// 파일명 = Trend_&lt;title&gt;_&lt;yyyyMMdd_HHmmss&gt;.xlsx. antiforgery 미적용 평범한 POST.
    /// </summary>
    [HttpPost("export-excel")]
    public IActionResult ExportExcel([FromBody] TrendExcelModel req)
    {
        if (req is null)
            return BadRequest("model required");

        var bytes = TrendExcelExporter.BuildTrendExcel(req);
        var title = string.IsNullOrWhiteSpace(req.Title) ? "Trend" : SanitizeFileName(req.Title);
        var fileName = $"Trend_{title}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
        return File(bytes, TrendExcelExporter.XlsxMimeType, fileName);
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}
