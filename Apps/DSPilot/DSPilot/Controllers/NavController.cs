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

    public NavController(
        DsProjectService project,
        AppSettingsService settings,
        DspDbService db,
        PlcConnectionStatusTracker plcStatus,
        PlcPingService ping,
        HubSubscriberService hub,
        IUserTagAlertRepository alertRepo,
        AbnormalEventService abnormal,
        BlueprintService blueprint)
    {
        _project = project;
        _settings = settings;
        _db = db;
        _plcStatus = plcStatus;
        _ping = ping;
        _hub = hub;
        _alertRepo = alertRepo;
        _abnormal = abnormal;
        _blueprint = blueprint;
    }

    [HttpGet]
    public ActionResult<NavDto> Get()
    {
        var showPlcDebug = _settings.LoadSettings().Ui.ShowPlcDebug;

        // FlowProcessOrder: 대시보드에서 사용자가 지정한 공정 순서.
        var processOrder = _blueprint.Layout.FlowProcessOrder;
        var rankByName = processOrder
            .Select((o, i) => (o.FlowName, i))
            .ToDictionary(x => x.FlowName, x => x.i, StringComparer.OrdinalIgnoreCase);

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
                    systems.Add(new NavSystemDto(system.Name, sorted));
                }
            }
        }

        return new NavDto(showPlcDebug, systems);
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
                    s.Name, s.Vendor, s.IpAddress, s.Port, s.IsConnected, s.LastError))
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
                    .Select(p => new NavPlcAdapterDto(p.Name, p.Vendor, p.Ip, p.Port, p.Connected, p.Error))
                    .ToList();
            }
            else
            {
                plcSource = "none"; // 대상 PLC 미설정 — 핑할 곳이 없음.
                plcTotal = plcConnected = plcDisconnected = 0;
                adapters = new List<NavPlcAdapterDto>();
            }
        }

        var agent = new NavAgentDto(hubState, plcTotal, plcConnected, plcDisconnected, plcSource, adapters);

        // ── anomalyActiveCount (이상발생 활성) ── 최근 10분 Error. ack 가 창 안이면 시작점을 ack 로 당김.
        var nowUtc = DateTime.UtcNow;
        var anomalyFromUtc = nowUtc - TimeSpan.FromMinutes(10);
        if (anomalyAck.HasValue && anomalyAck.Value.UtcDateTime > anomalyFromUtc)
            anomalyFromUtc = anomalyAck.Value.UtcDateTime;
        var anomalyActiveCount = await _alertRepo.CountAlertsAsync(
            anomalyFromUtc, nowUtc, null, "Error", null, ct);

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
            _db.HasData,
            anomalyActiveCount,
            recentAnomalies,
            DateTimeOffset.UtcNow);
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

public record NavDto(bool ShowPlcDebug, List<NavSystemDto> Systems);

public record NavSystemDto(string Name, List<string> Flows);

public record NavSummaryDto(
    NavLinesDto Lines,
    NavAgentDto Agent,
    bool HasData,
    int AnomalyActiveCount,
    List<NavAnomalyDto> RecentAnomalies,
    DateTimeOffset ServerTimeUtc);

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
    List<NavPlcAdapterDto> Adapters);

public record NavPlcAdapterDto(
    string Name, string Vendor, string Ip, int Port, bool Connected, string? Error);
