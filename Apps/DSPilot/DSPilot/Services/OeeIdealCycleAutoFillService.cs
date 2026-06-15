// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace DSPilot.Services;

/// <summary>
/// 표준CT(idealCT) 실측 자동 1회 기입 — uptime/OEE 의 성능(Performance)을 수동 입력 없이 산출 가능하게 한다.
///
/// Flow 별로 idealCT 가 비어 있고 이상치 제외 클린사이클(IsIdle=0, ct&gt;0)이 MinCleanCycles 이상 모이면,
/// 표준CT 추천 테이블(/api/oee/ideal-cycle/table)과 동일 공식(<see cref="OeeCtStatsService"/>,
/// best-demonstrated 분위수 기본 p10)으로 산출한 값을 FlowCycleOverride.IdealCycleTimeMs 에 기입하고
/// 출처(IdealCycleTimeSource="auto")를 남긴다.
///
/// 1회성: 한 번 채워진 Flow 는 값이 비워지기 전까지 재기입하지 않는다(추세 흔들림 방지 — 값이 떠다니지 않음).
/// 사용자가 값을 지우면(=비우면) 다음 주기에 다시 채운다(재보정 경로). 수동 입력값은 절대 덮지 않는다
/// (<see cref="AppSettingsService.FillIdealCycleTimesAuto"/> 가 빈 칸만 채움 + Update 원자화로 레이스 안전).
///
/// 튜닝 노브는 IConfiguration "Oee" 섹션(무사이클 임계 NoCycleSeconds 와 동일 컨벤션 — 혼동 방지 분리):
///   Oee:AutoIdealCycle:Enabled        (기본 true)  자동 기입 사용 여부
///   Oee:AutoIdealCycle:MinCleanCycles (기본 30)    이 개수 이상 클린사이클 모인 Flow 만 — p10 이 통계적으로
///                                                  의미 있으려면 AutoCalibration(10)보다 보수적으로 잡는다.
///   Oee:AutoIdealCycle:Percentile     (기본 10)    best-demonstrated 분위수(추천 테이블 기본값과 동일).
/// </summary>
public sealed class OeeIdealCycleAutoFillService : BackgroundService
{
    private const int DefaultMinCleanCycles = 30;
    private const int DefaultMinMedianCycles = 5; // 임시 중앙값 기입 최소 샘플(p10 확정 전 — 너무 적으면 무의미).
    private const double DefaultPercentile = 10.0;
    private const int SampleLimit = 2000; // 추천 테이블 기본과 동일 — flow별 최근 N 사이클 윈도.

    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private readonly OeeCtStatsService _ctStats;
    private readonly AppSettingsService _settings;
    private readonly IConfiguration _configuration;
    private readonly IHubContext<MonitoringHub> _hub;
    private readonly ILogger<OeeIdealCycleAutoFillService> _logger;

    public OeeIdealCycleAutoFillService(
        OeeCtStatsService ctStats,
        AppSettingsService settings,
        IConfiguration configuration,
        IHubContext<MonitoringHub> hub,
        ILogger<OeeIdealCycleAutoFillService> logger)
    {
        _ctStats = ctStats;
        _settings = settings;
        _configuration = configuration;
        _hub = hub;
        _logger = logger;
    }

    private bool Enabled => _configuration.GetValue<bool?>("Oee:AutoIdealCycle:Enabled") ?? true;

    private int MinCleanCycles
    {
        get
        {
            var v = _configuration.GetValue<int?>("Oee:AutoIdealCycle:MinCleanCycles") ?? DefaultMinCleanCycles;
            return v > 0 ? v : DefaultMinCleanCycles;
        }
    }

    private int MinMedianCycles
    {
        get
        {
            var v = _configuration.GetValue<int?>("Oee:AutoIdealCycle:MinMedianCycles") ?? DefaultMinMedianCycles;
            return v > 0 ? v : DefaultMinMedianCycles;
        }
    }

    private double Percentile
    {
        get
        {
            var v = _configuration.GetValue<double?>("Oee:AutoIdealCycle:Percentile") ?? DefaultPercentile;
            return Math.Clamp(v, 0, 100);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "OeeIdealCycleAutoFillService starting (enabled={Enabled}, minCleanCycles={Min}, percentile=p{P})",
            Enabled, MinCleanCycles, Percentile);

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try { await TickAsync(stoppingToken); }
                catch (Exception ex) { _logger.LogWarning(ex, "[OEE-idealCT] auto-fill tick failed"); }

                try { await Task.Delay(PollInterval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        if (!Enabled) return;

        // 후보 사전 선별 — 빈 칸(기입) + 임시(auto-median) 승급만 후보로. 변경 없으면 파일 쓰기 자체를 하지 않는다.
        var settings = _settings.LoadSettings();
        var existingByFlow = settings.FlowCycle.Overrides
            .Where(o => !string.IsNullOrWhiteSpace(o.FlowName))
            .GroupBy(o => o.FlowName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var minClean = MinCleanCycles;
        var minMedian = MinMedianCycles;
        var stats = await _ctStats.ComputeAsync(SampleLimit, Percentile);

        var candidates = new List<(string FlowName, int IdealCycleTimeMs, string Source)>();
        foreach (var (flow, s) in stats)
        {
            // 무엇을 기입할지(p10 확정 / 중앙값 임시 / 없음)는 순수 함수 단일 소스로 결정.
            var (ms, src) = OeeMath.PickAutoIdealCycle(s.SampleCount, s.Recommended, s.Median, minClean, minMedian);
            if (ms is null) continue;

            existingByFlow.TryGetValue(flow, out var ov);
            if (ov?.IdealCycleTimeMs is > 0)
            {
                // 기존값 보존이 기본. 임시(auto-median)를 확정(auto)으로 승급하는 경우만(값이 바뀔 때) 후보.
                if (src == "auto"
                    && string.Equals(ov.IdealCycleTimeSource, "auto-median", StringComparison.OrdinalIgnoreCase)
                    && ov.IdealCycleTimeMs != ms.Value)
                    candidates.Add((flow, ms.Value, "auto"));
            }
            else
            {
                candidates.Add((flow, ms.Value, src!));
            }
        }
        if (candidates.Count == 0) return;

        var applied = _settings.FillIdealCycleTimesAuto(candidates);
        if (applied <= 0) return; // 선별~기입 사이 사용자가 채운 경우 — 기입 없음.

        var summary = string.Join(", ", candidates.Select(c => $"{c.FlowName}={c.IdealCycleTimeMs}ms({c.Source})"));
        _logger.LogInformation("[OEE-idealCT] 표준CT 자동 기입/승급 {Count}건 (p{P}, 확정≥{Min}/임시≥{Med}): {Summary}",
            applied, Percentile, minClean, minMedian, summary);

        // 열려 있는 uptime/oee 페이지가 즉시 재조회하도록(uptime 은 DatabaseRebuilt 수신 시 reload).
        try { await _hub.Clients.All.SendAsync("DatabaseRebuilt", ct); }
        catch (Exception ex) { _logger.LogDebug(ex, "[OEE-idealCT] SignalR broadcast 실패(비치명)"); }
    }
}
