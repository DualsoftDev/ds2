// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Models.Heatmap;
using DSPilot.Services;
using Microsoft.AspNetCore.Mvc;

namespace DSPilot.Controllers;

/// <summary>
/// 격리형 호스팅용 Heatmap(동작편차) 데이터 API.
///
/// 기존 Blazor 페이지(/heatmap)가 직접 @inject 로 호출하던 <see cref="HeatmapService"/> 를
/// 정적 페이지(/app/heatmap.html)가 fetch 로 사용할 수 있도록 얇게 래핑한다.
/// 신규 데이터 로직은 없으며(직렬화 경계일 뿐), HeatmapService 는 싱글톤이라 Blazor 와 동일 인스턴스를 공유한다.
/// </summary>
[ApiController]
[Route("api/heatmap")]
public class HeatmapController : ControllerBase
{
    private readonly HeatmapService _heatmap;

    public HeatmapController(HeatmapService heatmap) => _heatmap = heatmap;

    /// <summary>
    /// Flow 별 매트릭스 Heatmap 데이터(전체 기간, 사전 계산 통계).
    /// 색상 클래스(heatmap-*)는 전체 Call 의 min/max 기준으로 서버에서 계산되므로 그대로 내려보낸다
    /// — 단일 셀 값만으로는 클라이언트가 재계산할 수 없다.
    /// </summary>
    [HttpGet("data")]
    public async Task<ActionResult<List<FlowHeatmapGroup>>> GetData()
        => await _heatmap.GetHeatmapDataAsync();

    /// <summary>
    /// 특정 Call 의 실행 이력(plcTagLog 의 InTag↔OutTag rising edge 매칭).
    /// period: all | today | 7d | 30d (Blazor Heatmap 페이지의 기간 프리셋과 동일).
    /// 이상치 제외 필터는 클라이언트(heatmap.html)에서 적용한다 — 원본 회차를 모두 내려보낸다.
    /// </summary>
    [HttpGet("call-history")]
    public async Task<ActionResult<List<CallExecutionRecord>>> GetCallHistory(
        [FromQuery] Guid callId, [FromQuery] string period = "all")
    {
        if (callId == Guid.Empty)
            return BadRequest("callId is required.");

        var (start, end) = ResolvePeriod(period);
        return await _heatmap.GetCallExecutionHistoryAsync(callId, start, end, null);
    }

    // Blazor Heatmap.GetHistoryRange 와 동일한 기간 변환(서버 로컬 시간 기준 — DB 와 동일 타임존).
    private static (DateTime? start, DateTime? end) ResolvePeriod(string period)
    {
        var now = DateTime.Now;
        return period switch
        {
            "today" => (now.Date, now),
            "7d" => (now.Date.AddDays(-6), now),
            "30d" => (now.Date.AddDays(-29), now),
            _ => ((DateTime?)null, (DateTime?)null),
        };
    }
}
