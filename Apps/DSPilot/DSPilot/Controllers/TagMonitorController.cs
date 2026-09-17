// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Kpi;
using Microsoft.AspNetCore.Mvc;

namespace DSPilot.Controllers;

/// <summary>
/// 태그 모니터링 — UserTag 의 값과 추이. doc/30 §9.4.
/// <para>
/// UserTag 는 두 갈래다. 값 종류가 <b>Bit 인 것만 알람(에러)</b>이 되고, 나머지(Word · Real · String 등)는
/// 정보용이라 알람 목록에 끼지 않고 여기서 값과 추이로 본다. 정의는 사용자가 Promaker 에서 만들고
/// DSPilot 은 그 값 변화를 신호 표에 쌓아 보여 준다.
/// </para>
/// </summary>
[ApiController]
[Route("api/tag-monitor")]
public sealed class TagMonitorController : ControllerBase
{
    private readonly TagMonitorRepository _repo;

    public TagMonitorController(TagMonitorRepository repo) => _repo = repo;

    /// <summary>모니터링 대상 태그 목록. 마지막 값과 그 시각을 함께 준다.</summary>
    /// <param name="all">true 면 IO 맵 태그까지 전부. 기본은 UserTag 만.</param>
    /// <param name="infoOnly">true 면 Bit 를 빼고 정보용(수치·문자열) UserTag 만.</param>
    [HttpGet("tags")]
    public async Task<ActionResult<List<TagMonitorItemDto>>> Tags(
        [FromQuery] bool all = false, [FromQuery] bool infoOnly = false, CancellationToken ct = default)
    {
        var rows = await _repo.ListAsync(userTagsOnly: !all, excludeBit: infoOnly, ct);
        return Ok(rows.Select(t => new TagMonitorItemDto(
            t.Id, t.Address, t.Name, t.Label, t.DataType, t.Unit,
            t.IsUserTag, t.IsAlarmSource, t.SystemName,
            t.LastAtMs is long ms ? KpiTime.ToIso(ms) : null,
            t.LastValue)).ToList());
    }

    /// <summary>
    /// 한 태그의 값 추이. 구간 직전의 마지막 값이 첫 점으로 들어와 차트 왼쪽 끝이 비지 않는다.
    /// 신호는 변할 때만 기록되므로 점 사이는 계단으로 이어 그린다.
    /// </summary>
    [HttpGet("tags/{id:long}/trend")]
    public async Task<ActionResult<TagTrendDto>> Trend(
        long id,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int limit = 5000,
        CancellationToken ct = default)
    {
        var (fromMs, toMs) = ResolveRange(from, to);
        var pts = await _repo.TrendAsync(id, fromMs, toMs, Math.Clamp(limit, 10, 50_000), ct);
        var changes = await _repo.ChangeCountAsync(id, fromMs, toMs, ct);

        return Ok(new TagTrendDto(
            id,
            KpiTime.ToIso(fromMs),
            KpiTime.ToIso(toMs),
            changes,
            pts.Select(p => new TagPointDto(p.AtMs, KpiTime.ToIso(p.AtMs), p.Num, p.Text)).ToList()));
    }

    /// <summary>기본 구간은 최근 24시간. 들어온 값은 로컬로 본다.</summary>
    private static (long FromMs, long ToMs) ResolveRange(DateTime? from, DateTime? to)
    {
        var now = DateTime.Now;
        var t = to ?? now;
        var f = from ?? t.AddHours(-24);
        if (f.Kind == DateTimeKind.Unspecified) f = DateTime.SpecifyKind(f, DateTimeKind.Local);
        if (t.Kind == DateTimeKind.Unspecified) t = DateTime.SpecifyKind(t, DateTimeKind.Local);
        long fm = KpiTime.ToMs(f.ToUniversalTime());
        long tm = KpiTime.ToMs(t.ToUniversalTime());
        if (tm <= fm) tm = fm + 1;
        return (fm, tm);
    }
}

/// <param name="IsAlarmSource">Bit UserTag 만 true — 알람(에러)이 될 수 있는 태그다.</param>
public sealed record TagMonitorItemDto(
    long Id, string Address, string Name, string? Label, string DataType, string? Unit,
    bool IsUserTag, bool IsAlarmSource, string? SystemName, string? LastAt, string? LastValue);

public sealed record TagTrendDto(
    long TagId, string From, string To, int ChangeCount, List<TagPointDto> Points);

/// <param name="Num">숫자로 읽히는 값. 비트는 0/1.</param>
/// <param name="Text">문자열 태그의 원문. 숫자면 null 일 수 있다.</param>
public sealed record TagPointDto(long AtMs, string At, double? Num, string? Text);
