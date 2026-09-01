// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Repositories;
using DSPilot.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR.Client;

namespace DSPilot.Controllers;

/// <summary>
/// 격리형 호스팅용 Nav API — 정적 셸(/app/shell.js)이 Blazor 사이드바(NavMenu.razor)와
/// 동일한 사이드바를 그릴 수 있도록 데이터만 내려준다.
///   - GET /api/nav         : per-system flow 트리 + PLC 디버그 노출 여부 (구조, 1회 로드)
///   - GET /api/nav/summary : 라인요약(가동/대기/효율) + agent 통신(Hub/PLC) + 이상발생 활성건수
///     (라이브 — 셸은 주기 폴링, Blazor 는 서비스 이벤트 구독으로 갱신)
/// camelCase 자동(MVC 기본값).
/// </summary>
[ApiController]
[Route("api/nav")]
public class NavController : ControllerBase
{
    private readonly DsProjectService _project;
    private readonly AppSettingsService _settings;
    private readonly DspDbService _db;
    private readonly PlcConnectionStatusTracker _plcStatus;
    private readonly PlcPingService _ping;
    private readonly HubSubscriberService _hub;
    private readonly IUserTagAlertRepository _alertRepo;
    private readonly AbnormalEventService _abnormal;
    private readonly BlueprintService _blueprint;
    private readonly DemoAdminService _demoAdmin;
    private readonly SimulationEngineService _engine;
    private readonly PlcToCallMapperService _mapper;

    public NavController(
        DsProjectService project,
        AppSettingsService settings,
        DspDbService db,
        PlcConnectionStatusTracker plcStatus,
        PlcPingService ping,
        HubSubscriberService hub,
        IUserTagAlertRepository alertRepo,
        AbnormalEventService abnormal,
        BlueprintService blueprint,
        DemoAdminService demoAdmin,
        SimulationEngineService engine,
        PlcToCallMapperService mapper)
    {
        _engine = engine;
        _project = project;
        _settings = settings;
        _db = db;
        _plcStatus = plcStatus;
        _ping = ping;
        _hub = hub;
        _alertRepo = alertRepo;
        _abnormal = abnormal;
        _blueprint = blueprint;
        _demoAdmin = demoAdmin;
        _mapper = mapper;
    }

    [HttpGet]
    public ActionResult<NavDto> Get()
    {
        var showPlcDebug = _settings.LoadSettings().Ui.ShowPlcDebug;

        // 외부 도구 바로가기(설비박사 챗봇·ReverseAI PLCtoAASX)는 데모 전환(마스터 스위치)이 켜졌을 때,
        // 각 항목의 개별 노출 체크가 켜진 것만 내려준다(라벨·URL 은 /demo/admin 관리 패널에서 설정).
        // 데모 전환 off 면 빈 목록 — 사이드바에 흔적이 없다.
        var externalShortcuts = _demoAdmin.GetVisibleShortcuts()
            .Select(s => new NavShortcutDto(s.Label, s.Href, s.Icon))
            .ToList();

        // FlowProcessOrder: 대시보드에서 사용자가 지정한 공정 순서.
        // 같은 flow 이름이 여러 시스템에 중복 존재하는 모델(현장 #131~134 사례)이면 레이아웃에도 이름이
        // 중복 적재된다 — ToDictionary 는 여기서 throw 해 /api/nav 전체(=사이드바 트리)가 500 으로 죽는다.
        // 첫 등장 순위만 취한다(TryAdd).
        var processOrder = _blueprint.Layout.FlowProcessOrder;
        var rankByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < processOrder.Count; i++)
            rankByName.TryAdd(processOrder[i].FlowName, i);

        // 사이클 분기 — flow 별 분기 이름 목록(분기 활성 flow 만 항목 존재). 셸이 설비효율/가동시간 분석
        // 그룹에서 부모 행을 "부모_분기" 행들로 치환하는 데 쓴다(생산효율/추이는 부모 그대로 = 설계 규약).
        var branchSets = _settings.GetAllFlowBranchSets()
            .ToDictionary(s => s.FlowName, s => s.Branches.Select(b => b.Name).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var systems = new List<NavSystemDto>();
        if (_project.IsLoaded)
        {
            foreach (var system in _project.GetActiveSystems())
            {
                var flows = _project.GetFlows(system.Id);
                // NavMenu.razor 와 동일: flow 가 있는 시스템만 노출.
                if (flows.Count > 0)
                {
                    var sorted = flows
                        .OrderBy(f => rankByName.TryGetValue(f.Name, out var r) ? r : int.MaxValue)
                        .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(f => f.Name)
                        .ToList();
                    var flowBranches = sorted
                        .Where(branchSets.ContainsKey)
                        .ToDictionary(f => f, f => branchSets[f], StringComparer.OrdinalIgnoreCase);
                    systems.Add(new NavSystemDto(system.Name, sorted,
                        flowBranches.Count > 0 ? flowBranches : null));
                }
            }
        }

        return new NavDto(showPlcDebug, systems, externalShortcuts);
    }

    /// <summary>
    /// 사이드바 라이브 섹션용 집계. 신규 데이터 로직 없음 — 기존 서비스 상태를 직렬화 경계에서 합산만 한다.
    ///   - lines  : DspDbService 스냅샷의 Flow.State 로 가동(Going)/대기(나머지)/가동률 계산
    ///   - agent  : HubSubscriberService(허브 연결 상태) + PlcConnectionStatusTracker(어댑터 연결)
    ///   - anomalyActiveCount : 최근 10분 내 Error 레벨 알림 수 (UserTagsController 의 activeError 와 동일 정의)
    ///     anomalyAck(선택) = 클라이언트가 /uptime 을 마지막으로 본 시각(serverTimeUtc 에코백).
    ///     ack 이전 Error 는 배지 카운트에서 제외(읽음 처리) — 이력 피드(recentAnomalies)는 영향 없음.
    /// </summary>
    [HttpGet("summary")]
    public async Task<ActionResult<NavSummaryDto>> GetSummary(
        [FromQuery] DateTimeOffset? anomalyAck = null, CancellationToken ct = default)
    {
        // ── lines (라인요약) ──
        var flows = _db.Snapshot.Flows;
        var total = flows.Count;
        var running = flows.Count(f => f.State == "Going");
        var idle = total - running;
        var efficiencyPct = total > 0 ? (int)Math.Round(running * 100.0 / total) : 0;

        // ── agent (통신 상태) ──
        var hubState = HubStateString(_hub.CurrentStatus);

        // PLC 어댑터 상태 — 1순위: Promaker.Agent 가 Hub 로 보고한 상태(IP 포함). 보고가 없으면
        // (허브 끊김 또는 모니터링 비활성으로 PlcConnectionStatusTracker 캐시가 비어 있으면)
        // 2순위로 DSPilot 이 PlcConnection.json 의 대상 IP 에 직접 핑(TCP)을 던져 상태를 만든다.
        var plc = _plcStatus.CurrentStatuses;
        string plcSource;
        int plcTotal, plcConnected, plcDisconnected;
        List<NavPlcAdapterDto> adapters;

        // 어댑터 → 매칭 시스템 표기 — 현재 모델 AID(시스템별 엔드포인트 정본)와 ip:port 로 대조한다.
        //   반환: 시스템 이름 | ""(모델에 엔드포인트가 있는데 이 ip:port 는 없음 = 미매칭/stale 후보)
        //        | null(모델 미로드/AID 없음 — 대조 근거 자체가 없어 UI 는 표기 생략).
        List<PlcEndpointInfo> modelEndpoints;
        try { modelEndpoints = _project.GetPlcEndpoints(); }
        catch { modelEndpoints = new List<PlcEndpointInfo>(); }
        string? MatchSystem(string? ip, int port)
        {
            if (modelEndpoints.Count == 0) return null;
            var hit = modelEndpoints.FirstOrDefault(e =>
                string.Equals(e.Ip, ip?.Trim(), StringComparison.OrdinalIgnoreCase) && e.Port == port);
            return hit?.SystemName ?? "";
        }

        if (plc.Count > 0)
        {
            plcSource = "agent";
            plcTotal = plc.Count;
            plcConnected = plc.Count(s => s.IsConnected);
            plcDisconnected = plcTotal - plcConnected;
            adapters = plc
                .OrderBy(s => s.IsConnected) // 끊긴 어댑터를 위로
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Select(s => new NavPlcAdapterDto(
                    s.Name, s.Vendor, s.IpAddress, s.Port, s.IsConnected, s.LastError,
                    MatchSystem(s.IpAddress, s.Port)))
                .ToList();
        }
        else
        {
            var pings = await _ping.ProbeAsync(ct);
            if (pings.Count > 0)
            {
                plcSource = "ping";
                plcTotal = pings.Count;
                plcConnected = pings.Count(p => p.Connected);
                plcDisconnected = plcTotal - plcConnected;
                adapters = pings
                    .OrderBy(p => p.Connected) // 끊긴 어댑터를 위로
                    .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new NavPlcAdapterDto(p.Name, p.Vendor, p.Ip, p.Port, p.Connected, p.Error,
                        MatchSystem(p.Ip, p.Port)))
                    .ToList();
            }
            else
            {
                plcSource = "none"; // 대상 PLC 미설정 — 핑할 곳이 없음.
                plcTotal = plcConnected = plcDisconnected = 0;
                adapters = new List<NavPlcAdapterDto>();
            }
        }

        // 모델 주소 수신 커버리지 — 주소 오타/영역 불일치처럼 "연결은 정상인데 그 태그만 0 건"인 상태를
        // 상세 패널에서 바로 보게 한다(판정에는 미사용 — GetAddressCoverage 주석 참조). 인메모리 카운트라 저비용.
        var (addrExpected, addrSeen, addrMissing) = _engine.GetAddressCoverage();

        // 멀티 PLC: 같은 커버리지를 시스템(PLC)별로도 묶어 내려준다 — "어느 PLC 구간이 안 오는가"를
        // 상세 패널에서 바로 식별. 시스템이 2개 이상일 때만 의미가 있어 UI 가 그때만 그린다.
        var addrSystems = BuildAddressCoverageBySystem();

        var agent = new NavAgentDto(hubState, plcTotal, plcConnected, plcDisconnected, plcSource, adapters,
            addrExpected, addrSeen, addrMissing, addrSystems, ReadPlcScanMode());

        // ── anomalyActiveCount (이상발생 활성) ── 최근 10분 Error. ack 가 창 안이면 시작점을 ack 로 당김.
        var nowUtc = DateTime.UtcNow;
        var anomalyFromUtc = nowUtc - TimeSpan.FromMinutes(10);
        if (anomalyAck.HasValue && anomalyAck.Value.UtcDateTime > anomalyFromUtc)
            anomalyFromUtc = anomalyAck.Value.UtcDateTime;
        var anomalyActiveCount = await _alertRepo.CountAlertsAsync(
            anomalyFromUtc, nowUtc, null, "Error", null, null, ct);

        // ── recentAnomalies (이상코드 피드) ── 최신 N건(레벨 무관). 사이드바 '이상코드' 실시간 피드용.
        //   두 출처를 시각 내림차순으로 합류(동일 형상 NavAnomalyDto):
        //     - usertag        : UserTag 매칭 알림(userTagAlertLog)
        //     - ds-error-0..3  : v12 경로이탈 이상감지(AbnormalEventService 인메모리 링버퍼)
        const int FeedCount = 8;
        var recent = await _alertRepo.GetLatestAlertsAsync(FeedCount, ct);
        // a.OccurredAt = FromSqliteUtcString → Kind=Local(로컬 벽시계). 표시는 그대로(ToLocalTime no-op)지만
        // 정렬 키는 진짜 UTC 로 맞춰야 dsRows(OccurredAtUtc=진짜 UTC)와 뒤섞일 때 offset 만큼 밀리지 않는다.
        var userTagRows = recent.Select(a => (
            Utc: a.OccurredAt.ToUniversalTime(),
            Dto: new NavAnomalyDto(
                "usertag", a.LogLevel, a.Name, a.SystemName,
                a.OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), a.TagAddress)));

        var dsRows = _abnormal.GetRecent(FeedCount).Select(e => (
            Utc: e.OccurredAtUtc,
            Dto: new NavAnomalyDto(
                e.Source, e.Level, e.Label,
                string.IsNullOrEmpty(e.FlowName) ? e.WorkName : e.FlowName,
                e.OccurredAtLocal, e.KindName)));

        var recentAnomalies = userTagRows.Concat(dsRows)
            .OrderByDescending(x => x.Utc)
            .Take(FeedCount)
            .Select(x => x.Dto)
            .ToList();

        return new NavSummaryDto(
            new NavLinesDto(total, running, idle, efficiencyPct),
            agent,
            // "PLC 데이터 수신중" 은 실제 유입 최근성(창 기반)이지 누적 보유(HasData)가 아니다 —
            // PLC 가 끊기면 이 값이 false 로 떨어져 "어댑터 끊김 + 데이터 수신중" 모순을 없앤다.
            _db.IsReceivingLiveData,
            anomalyActiveCount,
            recentAnomalies,
            DateTimeOffset.UtcNow,
            // 유입 공백 경과(초) — 배지가 "데이터 대기"에 길이를 병기해 15초 순간 공백과 수 분 장애를 구분한다.
            _db.InboundGapSeconds);
    }

    /// <summary>
    /// 주소 수신 커버리지를 시스템(PLC)별로 그룹핑.
    /// 1순위: AID 원천의 주소→시스템 매핑(<see cref="DsProjectService.GetAddressSystemMap"/> — UserTag·
    /// 워드주소 포함 전체를 커버). 2순위: 주소→flow(CallMapper)→시스템 추정(AID systemRef 없는 구 모델).
    /// 어느 쪽에도 없는 주소는 '기타' 그룹. 모델 미로드/주소 0개면 빈 목록.
    /// 그룹 순서는 모델의 활성 시스템 순서, '기타'는 맨 뒤.
    /// </summary>
    private List<NavAddrSystemDto> BuildAddressCoverageBySystem()
    {
        const string EtcGroup = "기타";
        const int MissingSamplePerSystem = 8;

        var snapshot = _engine.GetAddressSeenSnapshot();
        if (snapshot.Count == 0 || !_project.IsLoaded) return new List<NavAddrSystemDto>();

        var addressToSystem = _project.GetAddressSystemMap();

        var flowToSystem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var systemOrder = new List<string>();
        foreach (var system in _project.GetActiveSystems())
        {
            var flows = _project.GetFlows(system.Id);
            if (flows.Count == 0) continue;
            systemOrder.Add(system.Name);
            foreach (var flow in flows)
                flowToSystem.TryAdd(flow.Name, system.Name);
        }
        if (systemOrder.Count == 0) return new List<NavAddrSystemDto>();

        var groups = new Dictionary<string, (int Expected, int Seen, List<string> Missing)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (address, seen) in snapshot)
        {
            string? systemName = null;
            if (addressToSystem is not null && addressToSystem.TryGetValue(address, out var byAid))
                systemName = byAid;
            if (systemName is null)
            {
                var flowName = _mapper.FindCallByTag("", address)?.FlowName;
                if (flowName is not null && flowToSystem.TryGetValue(flowName, out var byFlow))
                    systemName = byFlow;
            }
            systemName ??= EtcGroup;

            if (!groups.TryGetValue(systemName, out var g)) g = (0, 0, new List<string>());
            g.Expected++;
            if (seen) g.Seen++;
            else if (g.Missing.Count < MissingSamplePerSystem) g.Missing.Add(address);
            groups[systemName] = g;
        }

        var ordered = new List<NavAddrSystemDto>();
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in systemOrder)
            if (groups.TryGetValue(name, out var g) && emitted.Add(name))
                ordered.Add(new NavAddrSystemDto(name, g.Expected, g.Seen, g.Missing));
        // 활성(flow 보유) 시스템 목록 밖의 그룹 — UserTag 가 passive 시스템 소속인 경우 등. 이름순으로 뒤에.
        foreach (var kv in groups.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            if (!string.Equals(kv.Key, EtcGroup, StringComparison.Ordinal) && emitted.Add(kv.Key))
                ordered.Add(new NavAddrSystemDto(kv.Key, kv.Value.Expected, kv.Value.Seen, kv.Value.Missing));
        if (groups.TryGetValue(EtcGroup, out var etc))
            ordered.Add(new NavAddrSystemDto(EtcGroup, etc.Expected, etc.Seen, etc.Missing));
        return ordered;
    }

    // ── PLC 수집 방식 (Promaker 업로드 시 선택) ──
    // Promaker 가 업로드/PLAY 시 공유 폴더 agent/session.json 에 isRealPlcConnected 를 기록한다
    // (런타임 세팅 다이얼로그의 "PLC 읽기 방식" 라디오 — true=Agent 직접 스캔, false=Edge 단말(Pi5) 위임).
    // 네트워크/클라우드 업로드도 AgentUploadReceiver 가 같은 파일을 이 머신 공유 폴더에 배치하므로
    // DSPilot 은 읽기 전용으로 이 파일만 보면 된다. 필드 부재(구 session.json)는 기본 true(직접)와 동일.
    private static readonly object PlcScanModeLock = new();
    private static DateTime _plcScanModeStampUtc = DateTime.MinValue;
    private static string? _plcScanModeCached;

    /// <summary>"direct" | "delegated" | null(session.json 없음/손상 — 업로드 이력 없음).
    /// 4초 폴링 대상이라 파일 mtime 이 같으면 재파싱하지 않는다.</summary>
    private static string? ReadPlcScanMode()
    {
        var path = Path.Combine(Infrastructure.SharedPaths.AgentDirectory, "session.json");
        try
        {
            if (!System.IO.File.Exists(path)) return null;
            var stamp = System.IO.File.GetLastWriteTimeUtc(path);
            lock (PlcScanModeLock)
            {
                if (stamp == _plcScanModeStampUtc) return _plcScanModeCached;
                using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
                var direct = !doc.RootElement.TryGetProperty("isRealPlcConnected", out var v)
                             || v.ValueKind != System.Text.Json.JsonValueKind.False;
                _plcScanModeCached = direct ? "direct" : "delegated";
                _plcScanModeStampUtc = stamp;
                return _plcScanModeCached;
            }
        }
        catch
        {
            return null; // 부분-쓰기/손상 순간은 미상 처리 — 다음 폴링에서 재시도.
        }
    }

    // HubConnectionState → 셸/Blazor 가 동일하게 해석하는 소문자 토큰.
    private static string HubStateString(HubConnectionState state) => state switch
    {
        HubConnectionState.Connected => "connected",
        HubConnectionState.Connecting => "connecting",
        HubConnectionState.Reconnecting => "reconnecting",
        _ => "disconnected",
    };
}

// ── DTOs (camelCase 자동) ──

public record NavDto(bool ShowPlcDebug, List<NavSystemDto> Systems, List<NavShortcutDto> ExternalShortcuts);

// 사이드바 외부 도구 바로가기 1행(절대 URL). 데모 전환 활성 + 개별 노출 체크 시에만 내려온다.
public record NavShortcutDto(string Label, string Href, string Icon);

// FlowBranches: 분기 활성 flow → 분기 이름 목록(정의 순서). null/미포함 = 그 flow 분기 미사용.
public record NavSystemDto(string Name, List<string> Flows,
    Dictionary<string, List<string>>? FlowBranches = null);

public record NavSummaryDto(
    NavLinesDto Lines,
    NavAgentDto Agent,
    // 실제 PLC 유입 최근성(DspDbService.IsReceivingLiveData). 셸은 data.receivingData 로 읽는다.
    bool ReceivingData,
    int AnomalyActiveCount,
    List<NavAnomalyDto> RecentAnomalies,
    DateTimeOffset ServerTimeUtc,
    // 마지막 유입 이후 경과(초). null = 부팅 후 유입이 한 번도 없음(=계측 근거 없음).
    double? InboundGapSeconds = null);

// 사이드바 '이상코드' 피드 1행. Source = 출처("usertag" | 추후 "ds-error-1".."4").
public record NavAnomalyDto(
    string Source,
    string Level,
    string Label,
    string System,
    string OccurredAtLocal,
    string Code);

public record NavLinesDto(int Total, int Running, int Idle, int EfficiencyPct);

public record NavAgentDto(
    string Hub,
    int PlcTotal,
    int PlcConnected,
    int PlcDisconnected,
    /// <summary>PLC 어댑터 상태 출처 — "agent"(Promaker.Agent 보고) | "ping"(DSPilot 직접 TCP 핑) | "none"(대상 미설정).</summary>
    string PlcSource,
    List<NavPlcAdapterDto> Adapters,
    // 모델(AASX) 주소 수신 커버리지 — Expected=적힌 주소 수, Seen=부팅 후 1건 이상 수신한 주소 수,
    // Missing=미수신 주소 표본(상위 12개). 주소 오타/영역 불일치 진단용이며 판정에는 쓰지 않는다.
    int AddrExpected = 0,
    int AddrSeen = 0,
    List<string>? AddrMissing = null,
    // 멀티 PLC: 위 커버리지의 시스템(PLC)별 분해 — 시스템 2개 이상일 때 UI 가 시스템별 행으로 그린다.
    List<NavAddrSystemDto>? AddrSystems = null,
    // Promaker 업로드 시 선택한 PLC 수집 방식(session.json isRealPlcConnected) —
    // "direct"(Agent 직접 스캔) | "delegated"(Edge 단말 위임) | null(업로드 이력 없음/미상).
    string? PlcScanMode = null);

// 시스템(PLC) 1개의 주소 수신 커버리지. Missing 은 표본(시스템당 최대 8개).
public record NavAddrSystemDto(string System, int Expected, int Seen, List<string> Missing);

public record NavPlcAdapterDto(
    string Name, string Vendor, string Ip, int Port, bool Connected, string? Error,
    // 이 어댑터(ip:port)가 현재 모델 AID 에서 어느 시스템의 엔드포인트인지.
    //   시스템 이름 | ""(모델에 있는데 미매칭 — 구 모델 잔존/설정 불일치 후보) | null(모델 미로드/AID 없음 = 표기 생략).
    string? System = null);
