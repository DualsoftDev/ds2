// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Kpi;
using DSPilot.Models.TagMonitor;
using DSPilot.Services;
using Microsoft.AspNetCore.Mvc;

namespace DSPilot.Controllers;

/// <summary>
/// 태그 모니터링(/tag-monitor) 의 HTTP 표면.
/// <para>
/// 목록은 "무엇을 지켜보는가 + 지금 값", 추이는 "구간 안에서 어떻게 움직였는가" 하나씩이다.
/// 알람(<c>/api/user-tags</c>)과는 표를 나눠 쓴다 — 여기는 <c>signal</c>(값 변화), 저기는 <c>alert</c>(발화 사건).
/// </para>
/// </summary>
[ApiController]
[Route("api/tag-monitor")]
public sealed class TagMonitorController : ControllerBase
{
    private const int DefaultMaxPoints = 4000;

    private readonly TagMonitorService _svc;
    private readonly ILogger<TagMonitorController> _logger;

    public TagMonitorController(TagMonitorService svc, ILogger<TagMonitorController> logger)
    {
        _svc = svc;
        _logger = logger;
    }

    /// <summary>대상 목록 + 현재값. 화면이 2~5초로 폴링해 현재값 타일을 갱신한다.</summary>
    [HttpGet("tags")]
    public async Task<ActionResult<TagMonitorListDto>> GetTags(CancellationToken ct)
        => await _svc.GetTagsAsync(ct);

    /// <summary>
    /// 태그별 구간 시계열 + 통계. tagIds 는 쉼표로 구분한 tag.id 목록이다.
    /// <para>from/to 미지정이면 오늘 0시~현재. 통계는 솎기 전 전체 행으로 계산하므로 포인트를 줄여도 값은 참이다.</para>
    /// </summary>
    [HttpGet("series")]
    public async Task<ActionResult<TagMonitorSeriesResponseDto>> GetSeries(
        [FromQuery] string? tagIds,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? maxPoints,
        CancellationToken ct)
    {
        var ids = (tagIds ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => long.TryParse(s, out var v) ? v : -1)
            .Where(v => v > 0)
            .Distinct()
            .Take(20)   // 한 화면에 20개를 넘겨 그릴 일은 없다 — 사고성 대량 요청 차단.
            .ToList();

        var (fromMs, toMs) = ResolveRange(from, to);
        if (ids.Count == 0)
            return new TagMonitorSeriesResponseDto(fromMs, toMs, []);

        try
        {
            var series = await _svc.GetSeriesAsync(ids, fromMs, toMs, maxPoints ?? DefaultMaxPoints, ct);
            return new TagMonitorSeriesResponseDto(fromMs, toMs, series);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[TagMonitor] 추이 조회 실패 (tags={Ids})", string.Join(',', ids));
            return StatusCode(500, new { error = "추이를 읽지 못했습니다: " + ex.Message });
        }
    }

    /// <summary>쿼리의 로컬 시각 → epoch ms. 미지정이면 오늘 0시~현재(KpiController 와 같은 규약).</summary>
    private static (long FromMs, long ToMs) ResolveRange(DateTime? from, DateTime? to)
    {
        var now = DateTime.Now;
        var f = from ?? now.Date;
        var t = to ?? now;
        if (f.Kind == DateTimeKind.Unspecified) f = DateTime.SpecifyKind(f, DateTimeKind.Local);
        if (t.Kind == DateTimeKind.Unspecified) t = DateTime.SpecifyKind(t, DateTimeKind.Local);
        var fm = KpiTime.ToMs(f.ToUniversalTime());
        var tm = KpiTime.ToMs(t.ToUniversalTime());
        if (tm <= fm) tm = fm + 1;
        return (fm, tm);
    }
}
