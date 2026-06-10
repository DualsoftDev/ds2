// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Globalization;
using DSPilot.Models;
using DSPilot.Models.Oee;
using DSPilot.Repositories;

namespace DSPilot.Services;

/// <summary>
/// UserTag 기반 OEE 자동수집 폴러 (doc/21 §usertag).
///
/// 현장 PLC 신호(생산카운터·불량비트·고장비트·정지원인비트)를 AASX UserTag 로 정의하면 그 raw 값이
/// alert 와 무관하게 plcTagLog 에 그대로 적재된다(SimulationEngineService.HandleHubTagChanged →
/// PlcTagLogWriterService.FlushAsync). 이 폴러는 그 raw 시계열을 <see cref="IPlcRepository"/> 로 직접
/// 쿼리해(★userTagAlertLog 미사용 — "같은 주소 1정의"·"matchOp 1:1" 제약을 우회) OEE 입력을 수동 입력
/// 없이 채운다:
///   • 고장비트 rising(0→1) → 정지 onset(detectSource='usertag', isFailure=1, category='unplanned').
///     SourceLogId = 해당 rising 로그의 plcTagLog.id → (detectSource,sourceLogId) 부분유니크로 멱등.
///   • 고장비트 falling(1→0) → 해당 open 이벤트 자동 clear(CloseDowntimeAsync).
///   • onset 직전 정지원인비트 스냅샷(1인 것) → reasonCode 자동 분류(다중 1 시 Priority 최소 우선).
///   • 생산/불량 카운터(또는 펄스) → oeeProductionCount(source='plc') 주입(plc &gt; manual, 신호 없는
///     필드는 기존 manual 값 보존).
///
/// 기존 <see cref="OeeDowntimeStateMachine"/>(detectSource='nocycle')과 소스 구분으로 충돌 없이 공존.
/// 신호 미설정 Flow(OeeSignals.Flows)는 자동수집 비활성(빈 채로 둠 — 가짜값 금지, doc/21 §10 정직성).
///
/// 정밀도 한계(doc/21): 100ms 폴링·변경분만 기록 → 100ms 미만 펄스는 누락될 수 있어 고속라인은 counter
/// delta 를 권장. 타임스탬프는 DSPilot 수신시각(+250ms flush 지터). flush 실패 시 silent drop.
/// </summary>
public sealed class OeeUserTagPollerService : BackgroundService
{
    private const string DetectSource = "usertag";
    private const string UnplannedCategory = "unplanned";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    // 고장 rising 스캔 윈도 오버랩 — flush 지터(+250ms)/타임스탬프 경계의 엣지 누락 방지.
    // 멱등 INSERT((detectSource,sourceLogId) 유니크)라 오버랩 재검출은 무해(no-op).
    private static readonly TimeSpan ScanOverlap = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPlcRepository _plc;
    private readonly AppSettingsService _settings;
    private readonly DsProjectService _project;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OeeUserTagPollerService> _logger;

    // 고장 rising 스캔 워터마크(UTC). 매 tick 후 now 로 전진 → 다음 tick 은 [_lastFaultScanUtc - overlap, now]만 스캔.
    private DateTime _lastFaultScanUtc;

    public OeeUserTagPollerService(
        IServiceScopeFactory scopeFactory,
        IPlcRepository plc,
        AppSettingsService settings,
        DsProjectService project,
        IConfiguration configuration,
        ILogger<OeeUserTagPollerService> logger)
    {
        _scopeFactory = scopeFactory;
        _plc = plc;
        _settings = settings;
        _project = project;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// 첫 tick 의 고장 rising 룩백(시간). 서비스 다운 중/직전에 발생한 onset 을 best-effort 회수.
    /// 멱등이라 과스캔은 무해. 이보다 더 오래전에 시작돼 아직 진행 중인 고장은 회수되지 않는다(정직한 한계).
    /// </summary>
    private int InitialLookbackHours
    {
        get
        {
            var v = _configuration.GetValue<int?>("Oee:UserTagInitialLookbackHours") ?? 24;
            return v > 0 ? v : 24;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "OeeUserTagPollerService starting (poll={Poll}s, initialLookback={Lookback}h)",
            PollInterval.TotalSeconds, InitialLookbackHours);

        _lastFaultScanUtc = DateTime.UtcNow - TimeSpan.FromHours(InitialLookbackHours);

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try { await TickAsync(stoppingToken); }
                catch (Exception ex) { _logger.LogWarning(ex, "[OEE-usertag] poller tick failed"); }

                try { await Task.Delay(PollInterval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var flows = _settings.LoadSettings().OeeSignals.Flows;
        if (flows is null || flows.Count == 0) return;

        var nowUtc = DateTime.UtcNow;
        var faultScanFromUtc = DateTime.SpecifyKind(_lastFaultScanUtc - ScanOverlap, DateTimeKind.Utc);
        var systemByFlow = BuildFlowSystemMap();

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IOeeRepository>();

        foreach (var flow in flows)
        {
            if (string.IsNullOrWhiteSpace(flow.FlowName)) continue;
            try
            {
                await ProcessFaultBitsAsync(repo, flow, systemByFlow, faultScanFromUtc, nowUtc, ct);
                await ProcessProductionAsync(repo, flow, nowUtc, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[OEE-usertag] flow '{Flow}' processing failed", flow.FlowName);
            }
        }

        // 워터마크 전진. 일부 flow 가 transient 실패해도 전진시킨다(멱등 INSERT + 다음 tick 오버랩으로 회복;
        // 정체 시 무한 재스캔 방지). 단 오버랩보다 오래된 미처리 엣지는 회수 못함 — 드문 실패에 한정된 한계.
        _lastFaultScanUtc = nowUtc;
    }

    // ── 고장비트 onset/clear + 원인 자동분류 ──────────────────────────────
    private async Task ProcessFaultBitsAsync(
        IOeeRepository repo, OeeFlowSignalMap flow, IReadOnlyDictionary<string, string> systemByFlow,
        DateTime scanFromUtc, DateTime nowUtc, CancellationToken ct)
    {
        var faultAddrs = (flow.FaultBitAddresses ?? new List<string>())
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (faultAddrs.Count == 0) return;

        var systemName = systemByFlow.TryGetValue(flow.FlowName, out var sys) && !string.IsNullOrEmpty(sys)
            ? sys
            : flow.FlowName;

        // 1) onset — 고장신호 rising(0→1) 마다 정지 INSERT(멱등). 새로 INSERT 된 건만 원인 분류.
        foreach (var addr in faultAddrs)
        {
            var risings = await _plc.FindRisingEdgesWithLogIdAsync(addr, scanFromUtc, nowUtc);
            foreach (var edge in risings)
            {
                var id = await repo.InsertDowntimeAsync(new OeeDowntimeEvent
                {
                    SystemName = systemName,
                    FlowName = flow.FlowName,
                    DeviceName = addr,            // 어느 고장신호가 트리거했는지 — clear 매칭 키 겸 표시.
                    StartAt = edge.At,
                    EndAt = null,
                    ReasonCode = null,
                    Category = UnplannedCategory, // 고장 = 계획외. MTBF/MTTR 분모.
                    IsFailure = 1,
                    DetectSource = DetectSource,
                    SourceLogId = edge.LogId,     // (detectSource,sourceLogId) 부분유니크 → 멱등.
                    Note = null,
                }, ct);

                if (id > 0) // 0 = 이미 존재(멱등 충돌) → 분류/로그 재실행 불필요.
                {
                    await ClassifyByCauseAsync(repo, flow, id, edge.At, ct);
                    _logger.LogInformation(
                        "[OEE-usertag] fault onset: flow='{Flow}' addr={Addr} at {At:u} event#{Id}",
                        flow.FlowName, addr, edge.At.ToUniversalTime(), id);
                }
            }
        }

        // 2) clear — 현재 open 인 usertag 고장 이벤트를, 그 신호의 첫 falling(1→0)으로 마감.
        //    falling 스캔 윈도를 event.StartAt 부터 잡아 onset '1' 로그가 윈도에 포함되게 한다 →
        //    LAG seed 보정(첫 '0' 의 직전값이 '1' 로 인식되어 경계 누락/오검출 방지).
        var open = await repo.GetOpenEventsAsync(flow.FlowName, ct);
        var myOpen = open.Where(e =>
            e.DetectSource == DetectSource &&
            !string.IsNullOrEmpty(e.DeviceName) &&
            faultAddrs.Contains(e.DeviceName!, StringComparer.OrdinalIgnoreCase));

        foreach (var evt in myOpen)
        {
            var falls = await _plc.FindFallingEdgesAsync(evt.DeviceName!, evt.StartAt, nowUtc);
            DateTime? clearAt = falls.Where(t => t > evt.StartAt).Select(t => (DateTime?)t).FirstOrDefault();
            if (clearAt is null) continue;

            var n = await repo.CloseDowntimeAsync(evt.Id, clearAt.Value, ct);
            if (n > 0)
                _logger.LogInformation(
                    "[OEE-usertag] fault clear: flow='{Flow}' addr={Addr} event#{Id} at {At:u}",
                    flow.FlowName, evt.DeviceName, evt.Id, clearAt.Value.ToUniversalTime());
        }
    }

    /// <summary>onset 직전(이하) 정지원인비트 스냅샷에서 1인 비트 → reasonCode/category 자동 분류.</summary>
    private async Task ClassifyByCauseAsync(
        IOeeRepository repo, OeeFlowSignalMap flow, long eventId, DateTime onsetAt, CancellationToken ct)
    {
        var causes = (flow.CauseBits ?? new List<OeeCauseBit>())
            .Where(c => !string.IsNullOrWhiteSpace(c.Address) && !string.IsNullOrWhiteSpace(c.ReasonCode))
            .ToList();
        if (causes.Count == 0) return;

        var active = new List<OeeCauseBit>();
        foreach (var c in causes)
        {
            // 단일 주소 → 해당 태그의 onset 이하 최신 로그 1건. 값이 truthy 면 그 원인이 활성.
            var logs = await _plc.GetLatestLogsByAddressesBeforeAsync(new List<string> { c.Address.Trim() }, onsetAt);
            if (IsTruthy(logs.FirstOrDefault()?.Value)) active.Add(c);
        }
        if (active.Count == 0) return;

        // 다중 1 → Priority 최소 우선(동률이면 설정 순서 유지 = OrderBy 안정정렬).
        var chosen = active.OrderBy(c => c.Priority).First();
        var category = string.IsNullOrWhiteSpace(chosen.Category)
            ? UnplannedCategory
            : chosen.Category.Trim().ToLowerInvariant();
        // OeeController.Classify 와 동일 규칙: isFailure = (category == unplanned). 기본 unplanned → 고장 유지.
        var isFailure = string.Equals(category, UnplannedCategory, StringComparison.OrdinalIgnoreCase);
        await repo.ClassifyDowntimeAsync(eventId, chosen.ReasonCode.Trim(), category, isFailure, ct);
    }

    // ── 생산/불량 자동 주입 ────────────────────────────────────────────────
    private async Task ProcessProductionAsync(
        IOeeRepository repo, OeeFlowSignalMap flow, DateTime nowUtc, CancellationToken ct)
    {
        var prodCfg = flow.ProductionCounter;
        var rejCfg = flow.RejectCounter;
        var hasProd = prodCfg is not null && !string.IsNullOrWhiteSpace(prodCfg.Address);
        var hasRej = rejCfg is not null && !string.IsNullOrWhiteSpace(rejCfg.Address);

        // ★불량(reject) 신호가 없으면 기록하지 않는다. quality = good/total 는 reject 없이는 산출 불가인데,
        //   생산수만 행을 만들면 HasReject 가 켜져 가짜 100% 품질을 만든다(doc/21 정직성: 가짜값 금지).
        //   생산수 total 은 요약의 TotalCount(dspFlowHistory)와 별개로 quality 분모로만 쓰여 reject 없이는 소비처가 없다.
        if (!hasRej) return;

        // 로컬일 버킷 — OeeController.Production / OeeRepositoryAdapter.QueryProductionAsync 의 bucketDate(로컬일)와 정합.
        var today = DateTime.Now.Date;
        var bucketDate = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var dayStartUtc = DateTime.SpecifyKind(today, DateTimeKind.Local).ToUniversalTime();

        // total 은 생산 카운터가 있을 때만(없으면 null → 기존값 보존). reject 는 위 가드로 항상 산출.
        int? total = hasProd ? await ComputeSignalCountAsync(prodCfg!, dayStartUtc, nowUtc) : null;
        int reject = await ComputeSignalCountAsync(rejCfg!, dayStartUtc, nowUtc);

        // shift 는 자동수집 비대상(계획시간/시프트는 수동) → 기본 버킷(""). plc 가 manual 을 덮되,
        // total=null 이면 기존 manual total 보존. 읽기측(QueryProductionAsync)이 plc 우선 단일소스로 이중계상 방지.
        await repo.UpsertProductionFromPlcAsync(bucketDate, flow.FlowName.Trim(), "", total, reject, ct);
    }

    /// <summary>counter=누적값 delta(wrap/reset 보정), pulse=구간 rising edge 카운트.</summary>
    private async Task<int> ComputeSignalCountAsync(OeeCounterSignal cfg, DateTime startUtc, DateTime endUtc)
    {
        var addr = cfg.Address.Trim();

        if (string.Equals(cfg.Kind, "pulse", StringComparison.OrdinalIgnoreCase))
        {
            var edges = await _plc.FindRisingEdgesAsync(addr, startUtc, endUtc);
            return edges.Count;
        }

        // counter (기본): baseline(start 이전 최신값) + 구간 표본 누적.
        var baselineLogs = await _plc.GetLatestLogsByAddressesBeforeAsync(new List<string> { addr }, startUtc);
        double? baseline = TryNumeric(baselineLogs.FirstOrDefault()?.Value);

        var rangeLogs = await _plc.GetTagLogsByAddressInRangeAsync(addr, startUtc, endUtc);
        var samples = rangeLogs
            .Select(l => TryNumeric(l.Value))
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToList();

        return OeeCounterDelta.Accumulate(baseline, samples, cfg.Width);
    }

    /// <summary>flow 이름 → 소속 system 이름. AASX 미로드 시 빈 맵. (OeeDowntimeStateMachine 과 동일.)</summary>
    private Dictionary<string, string> BuildFlowSystemMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (!_project.IsLoaded) return map;
            foreach (var sys in _project.GetActiveSystems())
                foreach (var f in _project.GetFlows(sys.Id))
                    map[f.Name] = sys.Name;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[OEE-usertag] flow→system map build failed");
        }
        return map;
    }

    /// <summary>SQL 정규화(lower/trim, '1'|'true'|'on')와 동일한 비트 truthy 판정.</summary>
    private static bool IsTruthy(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return false;
        var s = v.Trim().ToLowerInvariant();
        return s is "1" or "true" or "on";
    }

    private static double? TryNumeric(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        return double.TryParse(v.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
            ? d
            : null;
    }
}

/// <summary>
/// 누적 카운터(%MW/%MD)의 구간 생산량 산출 — wrap-around / 시프트 리셋 보정 (작업 항목 4).
/// baseline(구간 시작 이전 최신값) 뒤로 시간순 표본을 훑어 연속 delta 를 누적한다:
///   • delta &gt;= 0 : 정상 증가분 누적.
///   • delta &lt;  0 : 설정된 width(16|32)의 wrap 경계에 부합하면 그 모듈러스로 보정,
///                     그 외(또는 width 미지정)는 리셋으로 본다 — 현재값이 0 근처면 리셋 직후 생산분(=cur)만
///                     계상하고, 부분 감소/글리치/큰 점프는 0 으로 두어(이후 양수 delta 가 회복분 계상)
///                     전체 카운터값을 통째 phantom 으로 주입하지 않는다.
/// width 추측 금지: width 가 없으면 32-bit 카운터의 리셋을 16-bit wrap 으로 오판할 수 있어, 추측 대신 리셋
/// 우선(안전측)으로 처리한다 — 정확한 wrap 보정이 필요하면 OeeCounterSignal.Width 를 명시한다.
/// 한계(doc/21): 표본이 '변경분만'이라 polling 보다 빠른 wrap/배치 점프는 완벽히 복원되지 않는다 —
/// 상대/추세용으로 충분하나 절대 정확성은 보장하지 않는다.
/// </summary>
internal static class OeeCounterDelta
{
    private const long Wrap16 = 65536L;
    private const long Wrap32 = 4294967296L;
    // '경계/0 근접' 판정 폭. wrap 은 직전값이 상한에서 이 폭 이내 + 현재값이 이 폭 이내일 때만. 리셋 계상도
    // 현재값이 이 폭 이내(=거의 0)일 때만 cur 를 계상.
    private const double WrapProximity = 4096.0;

    public static int Accumulate(double? baseline, IReadOnlyList<double> orderedSamples, int? width)
    {
        if (orderedSamples.Count == 0) return 0;

        double prev;
        int startIdx;
        if (baseline.HasValue)
        {
            prev = baseline.Value;
            startIdx = 0;
        }
        else
        {
            // baseline 부재(구간 시작 이전 로그 없음) → 첫 표본을 기준점으로. 그 이전 생산분은 알 수 없음(정직한 한계).
            prev = orderedSamples[0];
            startIdx = 1;
        }

        long total = 0;
        for (int i = startIdx; i < orderedSamples.Count; i++)
        {
            var cur = orderedSamples[i];
            var d = cur - prev;
            total += d >= 0 ? (long)d : CorrectNegative(prev, cur, width);
            prev = cur;
        }

        if (total < 0) total = 0;
        return total > int.MaxValue ? int.MaxValue : (int)total;
    }

    private static long CorrectNegative(double prev, double cur, int? width)
    {
        // 명시된 width 의 wrap 만 적용(폭 추측 안 함 → 32-bit 카운터 리셋을 16-bit wrap 으로 오판 방지).
        if (width == 16 && prev >= Wrap16 - WrapProximity && prev < Wrap16 && cur <= WrapProximity)
        {
            var inc = (long)(cur - prev) + Wrap16;
            if (inc >= 0) return inc;
        }
        if (width == 32 && prev >= Wrap32 - WrapProximity && cur <= WrapProximity)
        {
            var inc = (long)(cur - prev) + Wrap32;
            if (inc >= 0) return inc;
        }
        // 리셋 처리: 현재값이 0 근처면 리셋 직후 생산분(=cur)만 계상. 그 외(부분 감소/comms 글리치/큰 점프)는
        // 0 — 전체 현재 카운터값을 통째로 생산에 주입하지 않는다(phantom 방지). 회복분은 이후 양수 delta 가 계상.
        return cur > 0 && cur <= WrapProximity ? (long)cur : 0;
    }
}
