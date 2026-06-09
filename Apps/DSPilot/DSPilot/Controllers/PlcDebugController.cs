// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Globalization;
using DSPilot.Models.Plc;
using DSPilot.Services;
using Microsoft.AspNetCore.Mvc;

namespace DSPilot.Controllers;

/// <summary>
/// 격리형 호스팅용 PLC 디버그 API.
/// Blazor /plc-debug 가 쓰던 PlcDebugService(SQLite 로그 분석) + DsProjectService(로드 여부) +
/// PlcToCallMapperService(태그→Flow 매핑) 를 얇게 래핑. 차트 렌더는 /js/plc-debug.js (Chart.js) 그대로 재사용.
/// 핵심 차이: 이 페이지만 "쓰기 레이어"(파일 업로드)가 있다. 업로드는 [FromForm] IFormFile 로 받아
/// temp 디렉토리에 GUID.db 로 저장 후 PlcDebugService.SetDatabasePath 로 연결한다(antiforgery 미적용 멀티파트 POST).
/// 샘플링/숫자 파싱/레인 매핑/색상은 Blazor LoadChartData 와 동일하게 서버에서 계산해
/// /js/plc-debug.js 가 기대하는 datasets({x,y,rawValue}) + options(rangeStart/rangeEnd/chartHeight/lanes) 형태로 내려준다.
/// </summary>
[ApiController]
[Route("api/plc-debug")]
public class PlcDebugController : ControllerBase
{
    private const long MaxUploadBytes = 2L * 1024 * 1024 * 1024;

    private readonly PlcDebugService _debug;
    private readonly DsProjectService _project;
    private readonly PlcToCallMapperService _mapper;
    private readonly ILogger<PlcDebugController> _logger;

    public PlcDebugController(
        PlcDebugService debug,
        DsProjectService project,
        PlcToCallMapperService mapper,
        ILogger<PlcDebugController> logger)
    {
        _debug = debug;
        _project = project;
        _mapper = mapper;
        _logger = logger;
    }

    /// <summary>
    /// DB 파일 업로드 → temp(DSPilot_Debug)/GUID.db 로 저장 → PlcDebugService.SetDatabasePath 연결.
    /// Blazor OnDbFileSelected 의 저장 경로/연결 로직과 동일. antiforgery 미적용 멀티파트 POST.
    /// </summary>
    [HttpPost("upload")]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    public async Task<ActionResult<UploadResultDto>> Upload([FromForm] IFormFile file, CancellationToken ct)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "파일이 비어 있습니다." });

        if (file.Length > MaxUploadBytes)
            return BadRequest(new { error = "파일이 너무 큽니다(최대 2GB)." });

        var tempDir = Path.Combine(Path.GetTempPath(), "DSPilot_Debug");
        Directory.CreateDirectory(tempDir);
        var destPath = Path.Combine(tempDir, $"{Guid.NewGuid()}.db");

        await using (var fs = new FileStream(destPath, FileMode.Create))
        {
            await file.CopyToAsync(fs, ct);
        }

        if (!_debug.SetDatabasePath(destPath))
        {
            _logger.LogWarning("PLC Debug 업로드 DB 연결 실패: {Path}", destPath);
            return StatusCode(500, new { error = "DB 파일 연결에 실패했습니다." });
        }

        return new UploadResultDto(destPath, file.FileName);
    }

    /// <summary>이미 서버에 있는 DB 경로로 연결(업로드 없이). Blazor 의 기본 DB 경로 흐름과 동일.</summary>
    [HttpPost("set-db-path")]
    public ActionResult<UploadResultDto> SetDbPath([FromBody] SetDbPathRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Path))
            return BadRequest(new { error = "경로가 비어 있습니다." });

        if (!_debug.SetDatabasePath(req.Path))
            return StatusCode(500, new { error = "DB 파일 연결에 실패했습니다." });

        return new UploadResultDto(req.Path, Path.GetFileName(req.Path));
    }

    /// <summary>전체 태그 목록 + 태그→Flow 매핑 + Flow 옵션. Blazor LoadDatabaseInfo 의 태그/매핑 부분.</summary>
    [HttpGet("tags")]
    public async Task<ActionResult<TagsResponseDto>> GetTags()
    {
        var tags = await _debug.GetAllTagsAsync();

        var tagFlowNames = new Dictionary<int, string>();
        var flowSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_project.IsLoaded)
        {
            if (!_mapper.IsInitialized)
                _mapper.Initialize();

            if (_mapper.IsInitialized)
            {
                foreach (var tag in tags)
                {
                    var mapping = _mapper.FindCallByTag(tag.Name, tag.Address);
                    if (string.IsNullOrWhiteSpace(mapping?.FlowName))
                        continue;

                    tagFlowNames[tag.Id] = mapping!.FlowName;
                    flowSet.Add(mapping.FlowName);
                }
            }
        }

        var flowNames = flowSet
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var tagDtos = tags
            .Select(t => new TagDto(
                t.Id,
                t.Address,
                t.Name,
                t.DataType,
                tagFlowNames.TryGetValue(t.Id, out var fn) ? fn : null))
            .ToList();

        return new TagsResponseDto(tagDtos, flowNames);
    }

    /// <summary>DB 통계(전체 태그/로그 수). Blazor GetStatisticsAsync.</summary>
    [HttpGet("statistics")]
    public async Task<ActionResult<StatisticsDto>> GetStatistics()
    {
        var (totalTags, totalLogs) = await _debug.GetStatisticsAsync();
        return new StatisticsDto(totalTags, totalLogs);
    }

    /// <summary>로그 전체 시간 범위(가장 오래된/최신). datetime-local 바인딩용 'o'(로컬) 문자열.</summary>
    [HttpGet("log-time-range")]
    public async Task<ActionResult<LogTimeRangeDto>> GetLogTimeRange()
    {
        var (oldest, latest) = await _debug.GetLogTimeRangeAsync();
        return new LogTimeRangeDto(
            oldest?.ToString("o", CultureInfo.InvariantCulture),
            latest?.ToString("o", CultureInfo.InvariantCulture));
    }

    /// <summary>태그별 로그 개수. Dictionary&lt;int,int&gt; (키는 태그 Id, 그대로 직렬화).</summary>
    [HttpPost("log-counts")]
    public async Task<ActionResult<Dictionary<int, int>>> GetLogCounts([FromBody] TagIdsRequest req)
    {
        var ids = req.TagIds ?? new List<int>();
        if (ids.Count == 0)
            return new Dictionary<int, int>();

        return await _debug.GetLogCountsByTagAsync(ids);
    }

    /// <summary>
    /// 샘플링 로그 → /js/plc-debug.js 가 기대하는 datasets + options 로 변환.
    /// Blazor LoadChartData 의 레인/색상/숫자파싱/이진감지/실제범위 산출을 그대로 서버에서 수행.
    /// 주의: 클라이언트가 보낸 maxPointsPerTag 는 30000 포인트 예산으로 다시 클램프한다(GetEffectiveMaxPointsPerTag).
    /// </summary>
    [HttpPost("sampled-logs")]
    public async Task<ActionResult<SampledChartDto>> GetSampledLogs([FromBody] SampledLogsRequest req)
    {
        var requestedIds = req.TagIds ?? new List<int>();
        if (requestedIds.Count == 0)
            return new SampledChartDto(
                new List<ChartDatasetDto>(), new List<PlcLaneDto>(),
                null, null, 0, 0, 0, req.MaxPointsPerTag, GetChartHeightPx(0), "선택된 태그가 없습니다.");

        var tags = await _debug.GetAllTagsAsync();
        var tagById = tags.ToDictionary(t => t.Id);

        // Blazor: 주소 기준 정렬 후 선택된 것만.
        var orderedTagIds = tags
            .Where(t => requestedIds.Contains(t.Id))
            .OrderBy(t => t.Address, StringComparer.OrdinalIgnoreCase)
            .Select(t => t.Id)
            .ToList();

        if (orderedTagIds.Count == 0)
            return new SampledChartDto(
                new List<ChartDatasetDto>(), new List<PlcLaneDto>(),
                null, null, 0, 0, 0, req.MaxPointsPerTag, GetChartHeightPx(0), "현재 필터에서 선택된 태그가 없습니다.");

        var start = ParseLocal(req.Start);
        var end = ParseLocal(req.End);

        var effectiveMaxPoints = GetEffectiveMaxPointsPerTag(orderedTagIds.Count, req.MaxPointsPerTag);
        var logCounts = await _debug.GetLogCountsByTagAsync(orderedTagIds);
        var tagLogsMap = await _debug.GetSampledLogsAsync(orderedTagIds, start, end, effectiveMaxPoints);

        var datasets = new List<ChartDatasetDto>();
        var lanes = new List<PlcLaneDto>();
        var totalOriginalPoints = 0;
        var totalSampledPoints = 0;
        var activeTags = 0;
        DateTime? actualRangeStart = null;
        DateTime? actualRangeEnd = null;

        foreach (var tagId in orderedTagIds)
        {
            var tag = tagById[tagId];
            var logs = tagLogsMap.GetValueOrDefault(tagId, new List<PlcTagLogEntity>());

            if (logCounts.TryGetValue(tagId, out var originalCount))
                totalOriginalPoints += originalCount;

            if (logs.Count == 0)
                continue;

            var firstLogTime = logs[0].DateTime;
            var lastLogTime = logs[^1].DateTime;
            actualRangeStart = !actualRangeStart.HasValue || firstLogTime < actualRangeStart.Value ? firstLogTime : actualRangeStart;
            actualRangeEnd = !actualRangeEnd.HasValue || lastLogTime > actualRangeEnd.Value ? lastLogTime : actualRangeEnd;

            var laneIndex = activeTags++;
            var laneBase = laneIndex * 2.0;
            var laneCenter = laneBase + 0.8;
            var label = BuildTagLabel(tag);
            var color = BuildColor(laneIndex, Math.Max(1, orderedTagIds.Count));

            lanes.Add(new PlcLaneDto(laneCenter, label));

            var numericValues = logs
                .Select(log => TryParseNumericValue(log.Value, out var parsed) ? (double?)parsed : null)
                .Where(v => v.HasValue)
                .Select(v => v!.Value)
                .ToList();

            var isBinary = numericValues.Count > 0 && numericValues.All(IsBinaryValue);
            var minNumeric = numericValues.Count > 0 ? numericValues.Min() : 0d;
            var maxNumeric = numericValues.Count > 0 ? numericValues.Max() : 0d;

            var data = logs.Select(log => new ChartPointDto(
                log.DateTime.ToString("o", CultureInfo.InvariantCulture),
                MapValueToLane(log.Value, laneBase, minNumeric, maxNumeric, isBinary),
                log.Value ?? string.Empty)).ToList();

            totalSampledPoints += data.Count;

            datasets.Add(new ChartDatasetDto(
                label,
                data,
                color,
                color,
                isBinary ? 2.4 : 1.8,
                data.Count <= 160 ? 1.5 : 0,
                3,
                false,
                isBinary ? 0.0 : 0.18,
                isBinary ? "before" : null,
                true,
                true));
        }

        var chartHeight = GetChartHeightPx(activeTags);

        if (activeTags == 0 || !actualRangeStart.HasValue || !actualRangeEnd.HasValue)
        {
            return new SampledChartDto(
                datasets, lanes, null, null,
                totalOriginalPoints, totalSampledPoints, 0,
                effectiveMaxPoints, chartHeight,
                $"선택 태그 {requestedIds.Count:N0}개 중 현재 기간에 로그가 있는 태그가 없습니다.");
        }

        var samplingInfo =
            $"선택 태그 {orderedTagIds.Count:N0}개 중 활성 {activeTags:N0}개, 전체 기간 {GetRangeLabel(actualRangeStart, actualRangeEnd)}, " +
            $"원본 {totalOriginalPoints:N0}개 -> 렌더 {totalSampledPoints:N0}개, 태그당 최대 {effectiveMaxPoints:N0}개";

        return new SampledChartDto(
            datasets,
            lanes,
            actualRangeStart.Value.ToString("o", CultureInfo.InvariantCulture),
            actualRangeEnd.Value.ToString("o", CultureInfo.InvariantCulture),
            totalOriginalPoints,
            totalSampledPoints,
            activeTags,
            effectiveMaxPoints,
            chartHeight,
            samplingInfo);
    }

    // ── Blazor 의 시간 파싱: datetime-local('yyyy-MM-ddTHH:mm:ss') → Local DateTime ──
    private static DateTime? ParseLocal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
            return DateTime.SpecifyKind(dt, DateTimeKind.Local);

        return null;
    }

    // ── 아래 헬퍼는 PlcDebug.razor @code 와 1:1 동일 ──

    private static string GetRangeLabel(DateTime? start, DateTime? end)
    {
        if (!start.HasValue || !end.HasValue)
            return "-";

        var span = end.Value - start.Value;
        if (span.TotalDays >= 1) return $"{span.TotalDays:F1} day";
        if (span.TotalHours >= 1) return $"{span.TotalHours:F1} hr";
        if (span.TotalMinutes >= 1) return $"{span.TotalMinutes:F1} min";
        return $"{span.TotalSeconds:F1} sec";
    }

    private static int GetEffectiveMaxPointsPerTag(int selectedTagCount, int requestedMaxPoints)
    {
        const int totalPointBudget = 30000;
        if (selectedTagCount <= 0)
            return requestedMaxPoints;

        var budgetPerTag = Math.Max(40, totalPointBudget / selectedTagCount);
        return Math.Min(requestedMaxPoints, budgetPerTag);
    }

    private static int GetChartHeightPx(int laneCount)
    {
        var rowHeight = laneCount switch
        {
            <= 120 => 28,
            <= 240 => 22,
            <= 400 => 18,
            <= 700 => 14,
            _ => 12
        };

        return Math.Clamp(laneCount * rowHeight + 120, 720, 12000);
    }

    private static string BuildColor(int index, int total)
    {
        var hue = total <= 0 ? 210 : (index * 360.0 / total) % 360;
        return string.Create(CultureInfo.InvariantCulture, $"hsl({hue:F0}, 72%, 46%)");
    }

    private static string BuildTagLabel(PlcTagEntity tag)
        => string.IsNullOrWhiteSpace(tag.Name) ? tag.Address : $"{tag.Address} | {tag.Name}";

    private static bool TryParseNumericValue(string? value, out double parsed)
    {
        parsed = 0;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();

        if (bool.TryParse(trimmed, out var boolValue))
        {
            parsed = boolValue ? 1 : 0;
            return true;
        }

        if (string.Equals(trimmed, "on", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "high", StringComparison.OrdinalIgnoreCase))
        {
            parsed = 1;
            return true;
        }

        if (string.Equals(trimmed, "off", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "low", StringComparison.OrdinalIgnoreCase))
        {
            parsed = 0;
            return true;
        }

        return double.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed) ||
               double.TryParse(trimmed, NumberStyles.Any, CultureInfo.CurrentCulture, out parsed);
    }

    private static bool IsBinaryValue(double value)
        => Math.Abs(value) < 0.000001 || Math.Abs(value - 1) < 0.000001;

    private static double MapValueToLane(string? rawValue, double laneBase, double minNumeric, double maxNumeric, bool isBinary)
    {
        const double low = 0.18;
        const double high = 1.42;

        if (!TryParseNumericValue(rawValue, out var value))
            return laneBase + 0.8;

        if (isBinary)
            return laneBase + (value > 0.5 ? high : low);

        if (Math.Abs(maxNumeric - minNumeric) < 0.000001)
            return laneBase + 0.8;

        var normalized = (value - minNumeric) / (maxNumeric - minNumeric);
        normalized = Math.Clamp(normalized, 0, 1);
        return laneBase + low + normalized * (high - low);
    }
}

// ── DTOs (positional records, camelCase 자동) ──

/// <summary>업로드/경로지정 결과.</summary>
public record UploadResultDto(string Path, string FileName);

public record SetDbPathRequest(string Path);

public record TagIdsRequest(List<int> TagIds);

public record SampledLogsRequest(List<int> TagIds, string? Start, string? End, int MaxPointsPerTag);

/// <summary>태그 한 건. flowName 은 매핑 없으면 null.</summary>
public record TagDto(int Id, string Address, string Name, string DataType, string? FlowName);

public record TagsResponseDto(List<TagDto> Tags, List<string> FlowNames);

public record StatisticsDto(int TotalTags, long TotalLogs);

/// <summary>로그 시간 범위. 'o' 라운드트립 로컬 문자열(datetime-local 슬라이스용) 또는 null.</summary>
public record LogTimeRangeDto(string? Oldest, string? Latest);

/// <summary>/js/plc-debug.js renderChart(point) 데이터 1건. {x,y,rawValue}.</summary>
public record ChartPointDto(string X, double Y, string RawValue);

/// <summary>/js/plc-debug.js 가 그대로 Chart.js dataset 으로 쓰는 형태.</summary>
public record ChartDatasetDto(
    string Label,
    List<ChartPointDto> Data,
    string BorderColor,
    string BackgroundColor,
    double BorderWidth,
    double PointRadius,
    double PointHoverRadius,
    bool Fill,
    double Tension,
    string? Stepped,
    bool SpanGaps,
    bool ShowLine);

/// <summary>y축 레인 라벨(태그명) 매핑. {value,label}. (CallTestController.LaneDto 와 충돌 회피 위해 Plc 접두).</summary>
public record PlcLaneDto(double Value, string Label);

/// <summary>샘플링 결과 전체 — renderChart(datasets, {rangeStart,rangeEnd,chartHeight,lanes}) 로 분해.</summary>
public record SampledChartDto(
    List<ChartDatasetDto> Datasets,
    List<PlcLaneDto> Lanes,
    string? RangeStart,
    string? RangeEnd,
    int TotalOriginalPoints,
    int TotalSampledPoints,
    int ActiveTagCount,
    int EffectiveMaxPointsPerTag,
    int ChartHeight,
    string SamplingInfo);
