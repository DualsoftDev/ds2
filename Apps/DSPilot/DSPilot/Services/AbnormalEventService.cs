using DSPilot.Hubs;
using DSPilot.Models.Dashboard;
using DSPilot.Models.UserTagAlerts;
using DSPilot.Repositories;
using Ds2.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FSharp.Core;

namespace DSPilot.Services;

/// <summary>
/// v12 경로이탈 이상감지(4종) 싱크 + 라이브 피드 (P5) + DB 영속화.
///
/// SimulationEngineService 의 MonitoringAbnormalAdapter 가 감지한 <see cref="AbnormalRecord"/> 를
/// <see cref="Record"/> 로 흘려보내면:
///   - 최근 N건 링버퍼에 적재 (대시보드 /api/dashboard/abnormals + 사이드바 /api/nav/summary 조회용)
///   - userTagAlertLog 에 INSERT (→ /uptime · /oee · 배지 카운트 자동 포함)
///   - SignalR "AbnormalDetected" 트리거 발행 → 화면이 REST 를 재조회 (코드베이스 관례: 이벤트=트리거)
/// </summary>
public sealed class AbnormalEventService
{
    // 링버퍼 용량 — 대시보드/사이드바는 상위 일부만 쓰지만, 짧은 폭주에도 최근 흐름을 잃지 않게 여유.
    private const int Capacity = 100;

    private readonly LinkedList<AbnormalEventDto> _recent = new();
    private readonly object _lock = new();
    private readonly IHubContext<MonitoringHub> _hub;
    private readonly IDspRepository _repo;
    private readonly DsProjectService _project;
    private readonly PlcToCallMapperService _tagMapper;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AppSettingsService _appSettings;
    private readonly ILogger<AbnormalEventService> _logger;

    public AbnormalEventService(
        IHubContext<MonitoringHub> hub,
        IDspRepository repo,
        DsProjectService project,
        PlcToCallMapperService tagMapper,
        IServiceScopeFactory scopeFactory,
        AppSettingsService appSettings,
        ILogger<AbnormalEventService> logger)
    {
        _hub = hub;
        _repo = repo;
        _project = project;
        _tagMapper = tagMapper;
        _scopeFactory = scopeFactory;
        _appSettings = appSettings;
        _logger = logger;
    }

    /// <summary>
    /// 엔진 싱크 진입점 — 엔진 스레드에서 동기 호출된다. 이름 해석(DB)과 SignalR 발행은
    /// fire-and-forget 으로 분리해 감지 경로를 막지 않는다.
    /// </summary>
    public void Record(AbnormalRecord rec) => _ = ProcessAsync(rec);

    /// <summary>최근 이상 N건(시각 내림차순). 대시보드/사이드바 REST 조회용. AbnormalAlarm.ResetIntervalHours 기준으로 오래된 항목 제외.</summary>
    public IReadOnlyList<AbnormalEventDto> GetRecent(int max)
    {
        var n = Math.Clamp(max, 1, Capacity);
        var hours = _appSettings.LoadSettings().AbnormalAlarm.ResetIntervalHours;
        var cutoff = hours > 0 ? DateTime.UtcNow - TimeSpan.FromHours(hours) : (DateTime?)null;
        lock (_lock)
        {
            var query = _recent.AsEnumerable();
            if (cutoff.HasValue)
                query = query.Where(e => e.OccurredAtUtc >= cutoff.Value);
            return query
                .OrderByDescending(e => e.OccurredAtUtc)
                .Take(n)
                .ToList();
        }
    }

    private async Task ProcessAsync(AbnormalRecord rec)
    {
        try
        {
            var (level, label) = Classify(rec.Kind);

            // Target.CallId → 모델상 (FlowName, WorkName, CallName). 경로 표시(FLOW/WORK/CALL) 용.
            string flow = string.Empty, work = string.Empty, callName = string.Empty;
            string? sensorTag = null;
            if (FsGuid(rec.Target.CallId) is Guid callId)
            {
                // AASX 프로젝트 모델에서 실제 이름 해석 (dspCall.WorkName 은 flow 명으로 채워지는 quirk 회피).
                var path = _project.IsLoaded ? _project.GetCallPath(callId) : null;
                if (path.HasValue)
                {
                    flow = path.Value.FlowName ?? string.Empty;
                    work = path.Value.WorkName ?? string.Empty;
                    callName = path.Value.CallName ?? string.Empty;
                }
                else
                {
                    // 폴백: dspCall 조회(FlowName 만이라도). IDspRepository/DspRepositoryAdapter 는 싱글톤이라 안전.
                    var info = await _repo.GetCallInfoAsync(callId);
                    if (info.HasValue)
                    {
                        work = info.Value.WorkName ?? string.Empty;
                        flow = info.Value.FlowName ?? string.Empty;
                    }
                }
                // Sensor* 에만: 이상을 트리거한 실제 InTag PLC 주소 해석
                if (rec.Kind is AbnormalKind.SensorShort or AbnormalKind.SensorOpen)
                    sensorTag = _tagMapper.GetCallTagsByCallId(callId)?.InTag;
            }

            var (systemId, systemName) = ResolveSystemInfo(flow);

            var dto = new AbnormalEventDto(
                Kind: (int)rec.Kind,
                KindName: rec.Kind.ToString(),
                Label: label,
                Level: level,
                Source: $"ds-error-{(int)rec.Kind}",
                FlowName: flow,
                WorkName: work,
                SystemName: systemName,
                ElapsedMs: FsInt(rec.ElapsedMs),
                Observed: FsBool(rec.Observed),
                OccurredAtUtc: rec.TimestampUtc,
                OccurredAtLocal: rec.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                SensorTag: sensorTag,
                CallName: callName);

            lock (_lock)
            {
                _recent.AddFirst(dto);
                while (_recent.Count > Capacity) _recent.RemoveLast();
            }

            await PersistToLogAsync(dto, systemId);

            // 코드베이스 관례: 페이로드 없는 트리거 → 클라이언트가 /api/dashboard/abnormals 재조회
            // (UserTagAlertsChanged 와 동일 패턴 — SignalR 직렬화 케이싱 차이 회피, REST 단일 소스).
            try { await _hub.Clients.All.SendAsync("AbnormalDetected"); }
            catch (Exception ex) { _logger.LogDebug(ex, "[Abnormal] SignalR 발행 실패 (non-critical)"); }

            _logger.LogInformation(
                "[Abnormal] {Kind} level={Level} flow={Flow} work={Work} elapsedMs={Elapsed}",
                dto.KindName, level, flow, work, dto.ElapsedMs);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Abnormal] 이벤트 처리 실패 (kind={Kind})", rec.Kind);
        }
    }

    // v12 §1 Kind → (DSPilot Level, 한글 라벨). 코어는 정책 독립(AbnormalSeverity/Response 는 P7) 이라
    // DSPilot 적용본을 여기서 정한다: 이상감지 4종 모두 Error 로 취급(운영 정책 — 오감지·과속도 즉시 조치 대상).
    private static (string Level, string Label) Classify(AbnormalKind kind) => kind switch
    {
        AbnormalKind.SensorOpen  => ("Error", "센서 단선/이탈"),
        AbnormalKind.SensorShort => ("Error", "센서 오감지"),
        AbnormalKind.ActionOver  => ("Error", "동작 지연(시간 초과)"),
        AbnormalKind.ActionUnder => ("Error", "동작 과속(시간 미만)"),
        _ => ("Warning", "이상"),
    };

    /// <summary>데모용 이상감지 이벤트 직접 주입 — 콘솔 테스트 전용. 실제 엔진 없이 링버퍼+SignalR 경로 검증.</summary>
    public async Task InjectDemoAsync(int kind, string flowName, string workName)
    {
        var abnKind = (AbnormalKind)Math.Clamp(kind, 0, 3);
        var (level, label) = Classify(abnKind);
        var now = DateTime.UtcNow;
        var (systemId, systemName) = ResolveSystemInfo(flowName);

        var dto = new AbnormalEventDto(
            Kind: (int)abnKind,
            KindName: abnKind.ToString(),
            Label: label,
            Level: level,
            Source: $"ds-error-{(int)abnKind}",
            FlowName: flowName,
            WorkName: workName,
            SystemName: systemName,
            ElapsedMs: abnKind is AbnormalKind.ActionOver or AbnormalKind.ActionUnder
                ? Random.Shared.Next(500, 8000) : null,
            Observed: abnKind is AbnormalKind.SensorOpen or AbnormalKind.SensorShort ? false : null,
            OccurredAtUtc: now,
            OccurredAtLocal: now.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));

        lock (_lock)
        {
            _recent.AddFirst(dto);
            while (_recent.Count > Capacity) _recent.RemoveLast();
        }

        await PersistToLogAsync(dto, systemId);

        try { await _hub.Clients.All.SendAsync("AbnormalDetected"); }
        catch (Exception ex) { _logger.LogDebug(ex, "[Abnormal] SignalR 발행 실패 (demo)"); }
    }

    /// <summary>링버퍼 전체 초기화 후 AbnormalDetected 트리거 — 데모 리셋 전용.</summary>
    public async Task ClearAsync()
    {
        lock (_lock) { _recent.Clear(); }
        try { await _hub.Clients.All.SendAsync("AbnormalDetected"); }
        catch { }
    }

    /// <summary>
    /// 이상감지 이벤트를 userTagAlertLog 에 INSERT.
    /// /uptime · /oee 의 /api/user-tags/snapshot 조회 + 배지 anomalyActiveCount 에 자동 포함.
    /// 필드 매핑:
    ///   Name      = 한글 라벨 (검색/표시 기준)
    ///   TagAddress = 경로 "WORK / CALL" (발생 위치 — Work 가 flow.worklocal 형태라 Flow 생략, 미해석 세그먼트 생략)
    ///   ValueType  = "Abnormal" (UserTag 구분자)
    ///   MatchOp    = "AbnormalDetect"
    ///   MatchValue = KindName (SensorOpen 등 종류 식별)
    ///   ActualValue = 동작 이상: "Nms" / 센서 이상: WorkName
    /// </summary>
    private async Task PersistToLogAsync(AbnormalEventDto dto, Guid systemId)
    {
        try
        {
            // Action* → "Nms" / Sensor* → InTag 주소(해석 성공 시) → WorkName(폴백)
            var actualValue = dto.ElapsedMs.HasValue
                ? $"{dto.ElapsedMs}ms"
                : (dto.SensorTag ?? dto.WorkName);

            var record = new UserTagAlertRecord(
                Id: 0,
                OccurredAt: DateTime.SpecifyKind(dto.OccurredAtUtc, DateTimeKind.Utc),
                SystemId: systemId,
                SystemName: string.IsNullOrEmpty(dto.SystemName) ? dto.FlowName : dto.SystemName,
                Name: dto.Label,
                LogLevel: dto.Level,
                TagAddress: BuildPath(dto.WorkName, dto.CallName),
                ValueType: "Abnormal",
                MatchOp: "AbnormalDetect",
                MatchValue: dto.KindName,
                ActualValue: actualValue ?? string.Empty,
                SourceLogId: null);

            using var scope = _scopeFactory.CreateScope();
            var alertRepo = scope.ServiceProvider.GetRequiredService<IUserTagAlertRepository>();
            await alertRepo.InsertAlertAsync(record);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Abnormal] userTagAlertLog INSERT 실패 (non-critical, kind={Kind})", dto.KindName);
        }
    }

    /// <summary>FlowName → (SystemId, SystemName). AASX 미로드 또는 미매칭 시 (Guid.Empty, "").</summary>
    private (Guid SystemId, string SystemName) ResolveSystemInfo(string flowName)
    {
        if (string.IsNullOrEmpty(flowName) || !_project.IsLoaded) return (Guid.Empty, string.Empty);
        try
        {
            foreach (var sys in _project.GetActiveSystems())
                foreach (var f in _project.GetFlows(sys.Id))
                    if (string.Equals(f.Name, flowName, StringComparison.Ordinal))
                        return (sys.Id, sys.Name);
        }
        catch { /* 비핵심 — 실패 시 빈값 반환 */ }
        return (Guid.Empty, string.Empty);
    }

    /// <summary>경로 "FLOW / WORK / CALL" 구성 — 빈 세그먼트·직전과 동일한 세그먼트(예 Work==Flow)는 생략.</summary>
    private static string BuildPath(params string?[] segments)
    {
        var parts = new List<string>();
        foreach (var s in segments)
        {
            if (string.IsNullOrWhiteSpace(s)) continue;
            var v = s.Trim();
            if (parts.Count > 0 && string.Equals(parts[^1], v, StringComparison.Ordinal)) continue;
            parts.Add(v);
        }
        return string.Join(" / ", parts);
    }

    private static Guid? FsGuid(FSharpOption<Guid> o) => FSharpOption<Guid>.get_IsSome(o) ? o.Value : null;
    private static int? FsInt(FSharpOption<int> o) => FSharpOption<int>.get_IsSome(o) ? o.Value : null;
    private static bool? FsBool(FSharpOption<bool> o) => FSharpOption<bool>.get_IsSome(o) ? o.Value : null;
}
