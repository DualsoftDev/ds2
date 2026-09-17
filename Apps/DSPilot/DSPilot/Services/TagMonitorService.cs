// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using Ds2.Editor;
using DSPilot.Infrastructure;
using DSPilot.Kpi;
using DSPilot.Models.TagMonitor;

namespace DSPilot.Services;

/// <summary>
/// 태그 모니터링 — AASX 의 UserTag 정의에 <c>signal</c> 표의 값을 얹어 현재값·추이·통계로 돌려준다.
///
/// <para>
/// 역할 분담: 값은 <see cref="TagMonitorRepository"/>(DB), 종류는 여기(AASX). 종류 축은 로그 레벨이고
/// (Error = 이상알람TAG, Info = 모니터링TAG) DB 에는 그 칸이 없다 — 같은 사실을 두 곳에 두지 않는다(doc/30 §9.1-3).
/// 목록은 두 종류를 <b>모두</b> 담는다. 이상알람TAG 도 값이 쌓이므로 그 추이를 못 볼 이유가 없고,
/// 화면은 기본 선택만 모니터링TAG 로 둔다.
/// </para>
/// <para>
/// ★ <b>계단식 읽기</b>는 저장소가 책임진다 — 구간 직전 마지막 값 하나를 구간 시작 시각으로 당겨 앞에 붙인다.
/// 그것이 없으면 "값이 안 변한 구간"과 "데이터가 없는 구간"이 화면에서 똑같아 보인다(doc/30 §9.4-2).
/// </para>
/// </summary>
public sealed class TagMonitorService
{
    /// <summary>한 태그가 한 번에 올리는 변화점 상한. 넘으면 솎고 화면에 잘림을 알린다.</summary>
    private const int ScanLimit = 100_000;

    private readonly TagMonitorRepository _repo;
    private readonly DsProjectService _project;
    private readonly ILogger<TagMonitorService> _logger;

    public TagMonitorService(TagMonitorRepository repo, DsProjectService project, ILogger<TagMonitorService> logger)
    {
        _repo = repo;
        _project = project;
        _logger = logger;
    }

    // ── 목록 ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 대상 목록 + 현재값. AASX 정의가 기준이고 <c>tag</c> 행이 있으면 id·단위·최신값을 붙인다.
    /// <list type="bullet">
    ///   <item>TagId=null : 정의는 있으나 아직 한 번도 수집되지 않았다(Agent 재시작 전·수집 대상 미포함).</item>
    ///   <item>InModel=false : 값은 쌓여 있는데 지금 모델엔 없는 주소다(AASX 에서 지운 태그의 과거 이력).</item>
    /// </list>
    /// </summary>
    public async Task<TagMonitorListDto> GetTagsAsync(CancellationToken ct = default)
    {
        List<TagInfo> dbRows;
        try
        {
            dbRows = await _repo.ListAsync(userTagsOnly: true, ct);
        }
        catch (Exception ex)
        {
            // 첫 기동이라 DB·표가 아직 없을 수 있다 — 정의만으로도 화면은 뜬다.
            _logger.LogWarning(ex, "[TagMonitor] 태그 목록 조회 실패 — 정의만 반환");
            dbRows = [];
        }

        // (System guid, 주소) 가 정본 키. 주소 단독은 소유자가 유일할 때만 쓰는 폴백이다 —
        // 둘 이상이면 아무 쪽이나 고르는 순간 다른 PLC 의 이력을 보여 주게 된다.
        var bySysAddr = new Dictionary<string, TagInfo>(StringComparer.OrdinalIgnoreCase);
        var byAddr = new Dictionary<string, TagInfo>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in dbRows)
        {
            if (!string.IsNullOrEmpty(r.SystemGuid)) bySysAddr[r.SystemGuid + "|" + r.Address] = r;
            if (!byAddr.TryAdd(r.Address, r)) ambiguous.Add(r.Address);
        }

        var tags = new List<TagMonitorTagDto>();
        var matched = new HashSet<long>();
        var projectLoaded = _project.IsLoaded;
        if (projectLoaded)
        {
            try
            {
                foreach (var r in _project.GetStore().GetAllUserTagsForProject())
                {
                    if (string.IsNullOrWhiteSpace(r.TagAddress)) continue;
                    var addr = r.TagAddress.Trim();
                    var guid = SystemKeyConvention.Key(r.SystemId);

                    TagInfo? hit = null;
                    if (guid.Length > 0) bySysAddr.TryGetValue(guid + "|" + addr, out hit);
                    if (hit is null && !ambiguous.Contains(addr)) byAddr.TryGetValue(addr, out hit);
                    if (hit is not null) matched.Add(hit.Id);

                    tags.Add(new TagMonitorTagDto(
                        TagId: hit?.Id,
                        SystemName: r.SystemName ?? hit?.SystemName ?? string.Empty,
                        SystemGuid: guid,
                        Name: string.IsNullOrWhiteSpace(r.Name) ? addr : r.Name,
                        Address: addr,
                        ValueType: UserTagEditorSupport.NormalizeValueType(r.ValueType) ?? "Bit",
                        Level: UserTagEditorSupport.NormalizeLevel(r.LogLevel),
                        Unit: hit?.Unit,
                        LastValue: hit?.LastValue,
                        LastAtMs: hit?.LastAtMs,
                        InModel: true));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[TagMonitor] UserTag 정의 조회 실패 — DB 행만 반환");
            }
        }

        // 모델에서 사라졌지만 값이 남아 있는 주소. 지우지 않고 보여 준다 — 과거 이력을 읽을 길이 있어야 한다.
        foreach (var r in dbRows)
        {
            if (matched.Contains(r.Id)) continue;
            tags.Add(new TagMonitorTagDto(
                TagId: r.Id,
                SystemName: r.SystemName ?? string.Empty,
                SystemGuid: r.SystemGuid ?? string.Empty,
                Name: string.IsNullOrWhiteSpace(r.Label) ? r.Address : r.Label!,
                Address: r.Address,
                ValueType: r.DataType,
                Level: UserTagEditorSupport.LevelMonitor,
                Unit: r.Unit,
                LastValue: r.LastValue,
                LastAtMs: r.LastAtMs,
                InModel: false));
        }

        return new TagMonitorListDto(projectLoaded, tags
            .OrderBy(t => t.SystemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    // ── 추이 ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 태그별 구간 시계열 + 통계.
    /// <para>통계는 <b>솎기 전 전체 변화점</b>으로 계산한다 — 포인트를 줄여도 최소·최대·ON 시간은 참값이다.
    /// 포인트가 <paramref name="maxPoints"/> 를 넘으면 고르게 솎되 처음과 끝은 남기고 Truncated 를 세운다.
    /// 조용히 자르면 화면이 "없는 사실"을 보여 준다(간트 2000캡 사건).</para>
    /// </summary>
    public async Task<List<TagMonitorSeriesDto>> GetSeriesAsync(
        IReadOnlyList<long> tagIds, long fromMs, long toMs, int maxPoints, CancellationToken ct = default)
    {
        var result = new List<TagMonitorSeriesDto>();
        if (tagIds.Count == 0) return result;
        if (maxPoints < 100) maxPoints = 100;

        List<TagInfo> meta;
        try { meta = await _repo.ListAsync(userTagsOnly: false, ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "[TagMonitor] 태그 메타 조회 실패"); return result; }
        var metaById = meta.ToDictionary(m => m.Id);

        foreach (var tagId in tagIds.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            if (!metaById.TryGetValue(tagId, out var m)) continue;

            // +1 을 더 받아 "상한에 걸렸는가"를 판별한다(딱 맞게 받으면 구분이 안 된다).
            var points = await _repo.TrendAsync(tagId, fromMs, toMs, ScanLimit + 1, ct);
            var scanTruncated = points.Count > ScanLimit;
            if (scanTruncated) points.RemoveRange(ScanLimit, points.Count - ScanLimit);

            // 저장소가 구간 직전 값을 fromMs 로 당겨 맨 앞에 넣어 준다 — 그것이 계단의 시작점이다.
            TagPoint? seed = points.Count > 0 && points[0].AtMs == fromMs ? points[0] : null;
            var isBit = string.Equals(m.DataType, "Bit", StringComparison.OrdinalIgnoreCase);
            var stats = ComputeStats(points, fromMs, toMs, isBit, seed is not null);

            var shown = Thin(points, maxPoints, out var thinned);
            result.Add(new TagMonitorSeriesDto(
                TagId: m.Id,
                Name: string.IsNullOrWhiteSpace(m.Label) ? m.Address : m.Label!,
                SystemName: m.SystemName ?? string.Empty,
                Address: m.Address,
                ValueType: m.DataType,
                Unit: m.Unit,
                StartValue: seed is null ? null : new TagMonitorPointDto(seed.AtMs, seed.Num, seed.Text),
                Points: shown.Select(p => new TagMonitorPointDto(p.AtMs, p.Num, p.Text)).ToList(),
                TotalCount: seed is null ? points.Count : points.Count - 1,
                Truncated: thinned || scanTruncated,
                Stats: stats));
        }
        return result;
    }

    /// <summary>
    /// 구간 통계. 계단값이므로 ON 시간은 "1 이 유지된 시간"이고, 시작값을 모르면(첫 기록 전) 그 앞 구간은 뺀다.
    /// Changes 는 구간 안에서 실제로 기록된 변화 횟수라 시작점은 세지 않는다.
    /// </summary>
    private static TagMonitorStatsDto ComputeStats(
        List<TagPoint> points, long fromMs, long toMs, bool isBit, bool hasSeed)
    {
        double? min = null, max = null;
        double sum = 0;
        var numCount = 0;
        for (var i = hasSeed ? 1 : 0; i < points.Count; i++)
        {
            if (points[i].Num is not { } v) continue;
            min = min is null ? v : Math.Min(min.Value, v);
            max = max is null ? v : Math.Max(max.Value, v);
            sum += v;
            numCount++;
        }

        long? onMs = null;
        if (isBit && points.Count > 0)
        {
            long acc = 0;
            var cursor = points[0].AtMs;
            var on = IsOn(points[0]);
            for (var i = 1; i < points.Count; i++)
            {
                if (points[i].AtMs > cursor)
                {
                    if (on) acc += points[i].AtMs - cursor;
                    cursor = points[i].AtMs;
                }
                on = IsOn(points[i]);
            }
            if (toMs > cursor && on) acc += toMs - cursor;
            onMs = acc;
        }

        var last = points.Count > 0 ? Display(points[^1]) : null;
        // ON 비율의 분모는 "값을 아는 시간"이다. 시작값을 모르면 첫 기록 전 구간은 분모에서도 뺀다.
        var denom = Math.Max(1, toMs - (hasSeed || points.Count == 0 ? fromMs : points[0].AtMs));
        return new TagMonitorStatsDto(
            Changes: hasSeed ? points.Count - 1 : points.Count,
            Min: min,
            Max: max,
            Avg: numCount > 0 ? sum / numCount : null,
            Last: last,
            OnMs: onMs,
            OnRatio: onMs is { } o ? Math.Round(o * 100.0 / denom, 2) : null);
    }

    private static bool IsOn(TagPoint p)
    {
        if (p.Num is { } n) return n != 0;
        var s = (p.Text ?? string.Empty).Trim();
        return s is "1" or "true" or "True" or "TRUE" or "on" or "On" or "ON";
    }

    private static string? Display(TagPoint p) =>
        p.Text ?? p.Num?.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>고르게 솎기 — 처음과 끝은 반드시 남긴다. 솎았으면 thinned=true.</summary>
    private static List<TagPoint> Thin(List<TagPoint> rows, int maxPoints, out bool thinned)
    {
        thinned = rows.Count > maxPoints;
        if (!thinned) return rows;
        var step = (double)(rows.Count - 1) / (maxPoints - 1);
        var picked = new List<TagPoint>(maxPoints);
        var lastIdx = -1;
        for (var i = 0; i < maxPoints; i++)
        {
            var idx = (int)Math.Round(i * step);
            if (idx <= lastIdx) idx = lastIdx + 1;
            if (idx >= rows.Count) break;
            picked.Add(rows[idx]);
            lastIdx = idx;
        }
        if (picked.Count > 0 && picked[^1].AtMs != rows[^1].AtMs) picked[^1] = rows[^1];
        return picked;
    }
}
