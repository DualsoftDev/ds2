// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Collections.Concurrent;
using System.Threading.Channels;
using Dapper;
using DSPilot.Infrastructure;
using DSPilot.Models;
using DSPilot.Repositories;
using Ds2.Backend.Common;
using Ds2.Core;
using Ds2.Editor;
using Ds2.Runtime.Engine;
using Ds2.Runtime.Engine.Abnormal;
using Ds2.Runtime.Engine.Core;
using Ds2.Runtime.Engine.Passive;
using Ds2.Runtime.IO;
using Ds2.Runtime.Model;
using Microsoft.Data.Sqlite;
using Microsoft.FSharp.Core;

namespace DSPilot.Services;

/// <summary>
/// Ds2.Runtime 엔진(EventDrivenEngine) + RuntimeModeSession + PassiveInferenceSession 을
/// 묶어서 보유하는 모니터링용 서비스.
/// HubSubscriberService 가 받은 OnTagChanged 신호를 여기로 위임하면
/// PassiveInference 가 Work/Call 상태를 추론 → 엔진 state 변경 →
/// CallStateChanged 이벤트 → dspCall DB UPDATE / DspDbService.EventWriter / FlowMetrics / SignalR 으로 흘러간다.
/// </summary>
public sealed class SimulationEngineService : IDisposable
{
    private readonly DsProjectService _projectService;
    private readonly IDspRepository _dspRepository;
    private readonly DspDbService _dspDbService;
    private readonly IFlowMetricsService _flowMetricsService;
    private readonly CallStateNotificationService _notificationService;
    private readonly IDatabasePathResolver _pathResolver;
    private readonly PlcTagLogWriterService _logWriter;
    private readonly AppSettingsService _settings;
    private readonly AbnormalEventService _abnormalEvents;
    private readonly ILogger<SimulationEngineService> _logger;

    private ISimulationEngine? _engine;
    private RuntimeModeSession? _runtimeSession;
    private PassiveInferenceSession? _passiveInference;
    private MonitoringAbnormalAdapter? _monitoringAbnormal;
    private readonly object _initLock = new();
    private bool _initFailed;

    // Welford 통계 — Going 시작 시각 + 누적 (count, mean, M2)
    private readonly Dictionary<Guid, (DateTime startedAt, int count, double mean, double m2)> _callStats = new();
    private readonly object _statsLock = new();

    // 완료 타임아웃으로 강제 Ready 복귀 중인 Call — Going→Ready 이벤트가 finish 통계/사이클로
    // 집계되지 않게(비정상 종료) 핸들러가 건너뛰도록 표시. _statsLock 으로 보호.
    private readonly HashSet<Guid> _timeoutAbandoned = new();

    // 동작편차 누적 통계 캡(MaxCallGoingTimeMs/MinCallGoingTimeMs) — 히스토리 재구성
    // (HeatmapService.ComputeExecutionRecordsAsync)과 동일 기준을 라이브 Welford 누산기에도 적용해,
    // 라인 정지·엣지 유실로 분 단위로 늘어진 Going 이 평균/표준편차를 오염(편차 수천 %)시키는 것을 차단.
    // 매 finish 마다 설정 디스크 재로드를 피하려 5초 TTL 캐시(런타임 설정 변경은 5초 내 반영).
    private readonly object _goingCapsLock = new();
    private long _goingCapsLoadedTick = long.MinValue;
    private int _maxCallGoingMs = 30000;
    private int _minCallGoingMs = 0;

    // 주소 → plcTag.id 캐시 (CycleTimeAnalysis 가 보는 plcTagLog INSERT 용).
    // AASX 재로딩 후 EnsureUserTagAddressesRegistered() 가 background thread 에서 갱신할 수 있으므로
    // ConcurrentDictionary — HandleHubTagChanged 의 lock-free read 와 안전하게 공존.
    private readonly ConcurrentDictionary<string, int> _plcTagIdByAddress = new(StringComparer.OrdinalIgnoreCase);

    // UserTag 주소 — HandleHubTagChanged 의 cache hit/miss 진단 한정 (전체 주소 로깅은 노이즈).
    // EnsureUserTagAddressesRegistered() 가 갱신.
    private volatile HashSet<string> _userTagAddressesForDiag = new(StringComparer.OrdinalIgnoreCase);

    // Flow Ready 전이 디바운스 — Call 들이 순차 실행되며 micro-gap 마다 Ready→Going 토글되는 점멸 방지
    private readonly Dictionary<string, CancellationTokenSource> _flowReadyDebounceCts = new();
    private readonly object _flowReadyLock = new();
    private const int FlowReadyDebounceMs = 600;

    // Phase 3 — head-start 엣지 유실 교차검증용. 래치 적격 flow 가 래치 닫힘인데 정상 Going Call(엔진 Going·
    // 이상치 Max 미초과)이 grace 이상 지속되면 head-start 누락으로 보고 래치를 연다(자가치유). 정상 진행 중인
    // Going 만 후보로 삼아(stuck-beyond-Max 제외) 워치독 abandon 과의 플랩을 방지. ReconcileStuckStatesAsync
    // (StateReconcile 단일 tick)에서만 접근 — 락 불요.
    private readonly Dictionary<string, DateTime> _latchEdgeLossSince = new(StringComparer.OrdinalIgnoreCase);
    private const int LatchEdgeLossGraceMs = 3000;

    // Engine 의 CallStateChanged 이벤트를 단일 컨슈머로 직렬화 — 같은 callGuid 의 빠른
    // Ready→Going / Going→Finish 가 fire-and-forget 으로 병렬 실행되어 Welford 통계가
    // (0,0,0) 으로 corruption 되던 race 차단. ResetAsync 에서 재생성 가능하도록 mutable.
    private Channel<CallStateChangedArgs>? _eventChannel;
    private CancellationTokenSource? _consumerCts;
    private Task? _consumerTask;

    public SimulationEngineService(
        DsProjectService projectService,
        IDspRepository dspRepository,
        DspDbService dspDbService,
        IFlowMetricsService flowMetricsService,
        CallStateNotificationService notificationService,
        IDatabasePathResolver pathResolver,
        PlcTagLogWriterService logWriter,
        AppSettingsService settings,
        AbnormalEventService abnormalEvents,
        ILogger<SimulationEngineService> logger)
    {
        _projectService = projectService;
        _dspRepository = dspRepository;
        _dspDbService = dspDbService;
        _flowMetricsService = flowMetricsService;
        _notificationService = notificationService;
        _pathResolver = pathResolver;
        _logWriter = logWriter;
        _settings = settings;
        _abnormalEvents = abnormalEvents;
        _logger = logger;
    }

    public bool IsInitialized => _engine is not null;

    /// <summary>
    /// 첫 신호 도착 시 lazy 초기화. 실패 시 false 반환.
    /// </summary>
    public bool TryEnsureInitialized()
    {
        if (_engine is not null) return true;
        if (_initFailed) return false;

        lock (_initLock)
        {
            if (_engine is not null) return true;
            if (_initFailed) return false;

            try
            {
                if (!_projectService.IsLoaded)
                {
                    _logger.LogInformation("[Engine] Project not loaded yet — deferring init");
                    return false;
                }

                var store = _projectService.GetStore();
                var index = SimIndexModule.build(store, 10);

                // monitoring 모드 — writeTag 콜백 없음 (DsPilot 은 모니터 전용, Hub 로 쓰지 않음)
                var noWriteTag = FSharpOption<FSharpFunc<string, FSharpFunc<string, Microsoft.FSharp.Core.Unit>>>.None;
                ISimulationEngine engine = new EventDrivenEngine(index, RuntimeMode.Monitoring, noWriteTag);

                engine.CallStateChanged += OnEngineCallStateChanged;
                engine.WorkStateChanged += OnEngineWorkStateChanged;

                var runtimeSession = new RuntimeModeSession(engine.Index, engine.IOMap, RuntimeMode.Monitoring);

                PassiveInferenceSession? passive = null;
                if (runtimeSession.RequiresPassiveInference)
                    passive = new PassiveInferenceSession(engine.Index, engine.IOMap, RuntimeMode.Monitoring, true);
                // v12 P5 이상감지: 로컬 MonitoringAbnormalAdapter(IO-edge, 상태추론 한계) 대신
                // Agent "OnAbnormal" SignalR 피드(ControlAbnormalAdapter, 실제 Going/Ready 기반) 사용.
                // HubSubscriberService.OnAbnormal → HandleHubAbnormal → _abnormalEvents.Record 경로.

                _engine = engine;
                _runtimeSession = runtimeSession;
                _passiveInference = passive;
                _monitoringAbnormal = null;

                // plcTag 행 부트스트랩 (CycleTimeAnalysis 데이터 소스 셋업)
                BootstrapPlcTags(engine.IOMap);

                // 재시작 시 누적 통계 corruption 방지 — DB 의 (count, mean, std) 를 Welford (count, mean, M2) 로 역산해서 시드
                SeedCallStatsFromDb();

                // 채널 + 단일 컨슈머 (재초기화 시 fresh 인스턴스로 시작)
                _eventChannel = Channel.CreateUnbounded<CallStateChangedArgs>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false,
                });
                _consumerCts = new CancellationTokenSource();
                var ch = _eventChannel;
                var ct = _consumerCts.Token;
                _consumerTask = Task.Run(() => ConsumeEngineEventsAsync(ch, ct));

                // Engine 정식 기동 — Status=Running 으로 전환하고 SimulationStatusChanged 이벤트 발사.
                // Monitoring 은 passive 모드 (Promaker 의 Runner.cs 와 동일 패턴) 라 Start() 사용.
                // StartWithHomingPhase() 는 능동 모드(Simulation/Control)에서만 의미가 있다.
                engine.Start();

                _logger.LogInformation(
                    "[Engine] Started — mode=Monitoring status={Status} passiveInference={Passive} hubSource={Source} plcTags={TagCount}",
                    engine.Status, passive is not null, runtimeSession.HubSource, _plcTagIdByAddress.Count);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Engine] Initialization failed");
                _initFailed = true;
                return false;
            }
        }
    }

    /// <summary>
    /// HubSubscriberService 가 받은 OnTagChanged 신호의 진입점.
    /// </summary>
    public void HandleHubTagChanged(string address, string value, string source)
    {
        if (!TryEnsureInitialized()) return;
        if (_runtimeSession is null) return;
        if (_runtimeSession.ShouldIgnoreHubSource(source)) return;

        // plcTagLog 기록 — 배치 writer 채널에 enqueue (실제 INSERT 는 PlcTagLogWriterService 가
        // 250ms / 100건 단위로 트랜잭션으로 처리)
        var inCache = _plcTagIdByAddress.TryGetValue(address, out var tagId);
        if (inCache)
            _logWriter.TryWrite(tagId, value, DateTime.Now);

        // 진단 — UserTag 정의 주소에 대해서만 hit/miss + enqueue 결과 로깅.
        if (_userTagAddressesForDiag.Contains(address))
        {
            _logger.LogInformation(
                "[Engine] UserTag hub signal {Addr}={Val} src={Src} cacheHit={Hit} tagId={Id}",
                address, value, source, inCache, inCache ? tagId : -1);
        }

        // 재연결 baseline(resync) — edge 가 아니라 PLC 재연결 직후의 현재값 스냅샷.
        // 단절 중 누락된 edge 를 전이로 재생하면 passive 추론/사이클 head·tail 이 오염되므로,
        // IO 현재값과 추론 기준선만 갱신하고 일반 observe 경로(HandleHubTag)는 타지 않는다.
        if (string.Equals(source, HubSource.Resync, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                _engine?.InjectIOValueByAddress(address, value);
                _passiveInference?.Baseline(address, value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Engine] resync baseline 적용 실패 {Addr}={Val}", address, value);
            }
            return;
        }

        RuntimeHubEffect[] effects;
        try
        {
            effects = _runtimeSession.HandleHubTag(address, value, source);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Engine] HandleHubTag threw for {Addr}={Val} src={Src}", address, value, source);
            return;
        }

        if (effects is null || effects.Length == 0) return;

        foreach (var effect in effects.OrderBy(e => e.DelayMs))
        {
            try { ApplySingleEffect(effect); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Engine] Effect {Kind} failed for {Addr}", effect.Kind, effect.Address);
            }
        }
    }

    /// <summary>
    /// DsStore 의 모든 IOTag (Out + In) + UserTag 주소를 plcTag 테이블에 INSERT — 한 번만.
    /// 캐시 _plcTagIdByAddress 채우기.
    /// UserTag 주소는 IOMap 에 안 들어가지만 Hub 가 Promaker 쪽에서 함께 broadcast 하므로
    /// 여기서도 plcTag 행을 만들어 줘야 plcTagLog INSERT 가 silent skip 되지 않는다
    /// (UserTagAlertService 의 폴링 데이터 소스).
    /// </summary>
    private void BootstrapPlcTags(SignalIOMap ioMap)
    {
        try
        {
            var dbPath = _pathResolver.GetSharedDbPath();
            var allAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in ioMap.Mappings)
            {
                if (!string.IsNullOrEmpty(m.OutAddress)) allAddresses.Add(m.OutAddress);
                if (!string.IsNullOrEmpty(m.InAddress)) allAddresses.Add(m.InAddress);
            }

            // UserTag 주소 추가 — IOMap 과 중복되면 HashSet 이 자동 dedup.
            var userTagAddrs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var store = _projectService.GetStore();
                foreach (var r in store.GetAllUserTagsForProject())
                {
                    if (!string.IsNullOrWhiteSpace(r.TagAddress))
                    {
                        var t = r.TagAddress.Trim();
                        allAddresses.Add(t);
                        userTagAddrs.Add(t);
                    }
                }
            }
            catch (Exception exUt)
            {
                _logger.LogWarning(exUt, "[Engine] UserTag 주소 수집 실패 (IOMap 만 plcTag 에 등록)");
            }

            // 진단용 UserTag 주소 집합 초기화 (HandleHubTagChanged 의 hit/miss 로깅 한정).
            _userTagAddressesForDiag = userTagAddrs;

            if (allAddresses.Count == 0) return;

            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using (var tx = conn.BeginTransaction())
            {
                const string upsert = @"
                    INSERT INTO plcTag (plcId, name, address, dataType)
                    VALUES (1, @Name, @Addr, 'BOOL')
                    ON CONFLICT(address) DO NOTHING";
                foreach (var addr in allAddresses)
                    conn.Execute(upsert, new { Name = addr, Addr = addr }, tx);
                tx.Commit();
            }

            // 캐시 채우기
            _plcTagIdByAddress.Clear();
            foreach (var row in conn.Query<(int Id, string Address)>("SELECT id, address FROM plcTag"))
                _plcTagIdByAddress[row.Address] = row.Id;

            // L3 — AASX 가 변경되어 plcTag 에 stale row 가 있을 수 있음.
            // FK 무결성 (plcTagLog.plcTagId 참조) 때문에 자동 삭제는 안 하고 경고만.
            // 사용자가 Settings → "DB 초기화" 로 정리 가능.
            var stale = _plcTagIdByAddress.Keys.Except(allAddresses, StringComparer.OrdinalIgnoreCase).ToArray();
            if (stale.Length > 0)
                _logger.LogWarning(
                    "[Engine] {Count} stale plcTag row(s) — address not in current AASX (예: {Sample}). " +
                    "Settings → \"DB 초기화\" 로 정리 권장.",
                    stale.Length, string.Join(", ", stale.Take(3)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Engine] BootstrapPlcTags failed");
        }
    }

    /// <summary>
    /// AASX 재로딩 후 새로 추가된 UserTag 주소를 plcTag 테이블 + 캐시에 등록.
    /// 부재 시 HandleHubTagChanged 가 캐시 miss 로 plcTagLog INSERT 를 silent skip 하여
    /// UserTagAlertService 의 폴링이 매칭 행을 찾지 못한다 (정의는 보이지만 기록이 안 되는 증상).
    /// UserTagAlertService.RefreshDefinitionsIfChanged 가 호출.
    /// </summary>
    public void EnsureUserTagAddressesRegistered()
    {
        if (_engine is null) return; // 엔진 미초기화 — 첫 신호 도착 시 BootstrapPlcTags 가 처리

        try
        {
            if (!_projectService.IsLoaded) return;

            var store = _projectService.GetStore();
            var allUserTagAddrs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var newAddresses = new List<string>();
            foreach (var r in store.GetAllUserTagsForProject())
            {
                if (string.IsNullOrWhiteSpace(r.TagAddress)) continue;
                var addr = r.TagAddress.Trim();
                allUserTagAddrs.Add(addr);
                if (!_plcTagIdByAddress.ContainsKey(addr))
                    newAddresses.Add(addr);
            }

            // 진단용 주소 집합 갱신 — newAddresses 가 비어도 (이미 캐시에 있어도) 늘 최신화.
            _userTagAddressesForDiag = allUserTagAddrs;

            if (newAddresses.Count == 0) return;

            var dbPath = _pathResolver.GetSharedDbPath();
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using (var tx = conn.BeginTransaction())
            {
                const string upsert = @"
                    INSERT INTO plcTag (plcId, name, address, dataType)
                    VALUES (1, @Name, @Addr, 'BOOL')
                    ON CONFLICT(address) DO NOTHING";
                foreach (var addr in newAddresses)
                    conn.Execute(upsert, new { Name = addr, Addr = addr }, tx);
                tx.Commit();
            }

            // 새로 INSERT 된 + 기존에 다른 시스템이 이미 넣어둔 행 모두 캐시에 반영.
            foreach (var row in conn.Query<(int Id, string Address)>(
                "SELECT id, address FROM plcTag WHERE address IN @Addrs",
                new { Addrs = newAddresses }))
            {
                _plcTagIdByAddress[row.Address] = row.Id;
            }

            _logger.LogInformation(
                "[Engine] Registered {Count} new UserTag address(es) for plcTagLog: {Sample}",
                newAddresses.Count, string.Join(", ", newAddresses.Take(5)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Engine] EnsureUserTagAddressesRegistered failed");
        }
    }

    /// <summary>
    /// 프로세스 재시작 후, dspCall 의 누적 통계 (GoingCount, AverageGoingTime, StdDevGoingTime) 를
    /// Welford 누적기 (count, mean, M2) 로 역산해서 _callStats 에 시드.
    /// 이게 없으면 재시작 후 첫 사이클이 누적 평균을 단일 값으로 OVERWRITE 함.
    ///   stddev = sqrt(M2 / n)  →  M2 = stddev² × n
    /// </summary>
    private void SeedCallStatsFromDb()
    {
        try
        {
            var dbPath = _pathResolver.GetSharedDbPath();
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();

            var rows = conn.Query<CallStatsSeedRow>(@"
                SELECT callId AS CallId,
                       GoingCount AS GoingCount,
                       AverageGoingTime AS AverageGoingTime,
                       StdDevGoingTime AS StdDevGoingTime
                FROM dspCall
                WHERE GoingCount > 0
                  AND AverageGoingTime IS NOT NULL
                  AND StdDevGoingTime IS NOT NULL");

            int seeded = 0;
            lock (_statsLock)
            {
                foreach (var r in rows)
                {
                    if (r.CallId == Guid.Empty) continue;
                    var m2 = r.StdDevGoingTime * r.StdDevGoingTime * r.GoingCount;
                    _callStats[r.CallId] = (default, r.GoingCount, r.AverageGoingTime, m2);
                    seeded++;
                }
            }

            if (seeded > 0)
                _logger.LogInformation(
                    "[Engine] Seeded {Count} call stats from DB (continuity across restart)", seeded);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Engine] SeedCallStatsFromDb failed — stats will start fresh");
        }
    }

    /// <summary>
    /// dspFlow.state 동기화 — Going 진입은 즉시, Going 이탈은 600ms 디바운스.
    /// 여러 Call 이 순차 실행되며 사이의 micro-gap 마다 Ready→Going 토글이 일어나 Dashboard 가
    /// 점멸하는 문제 차단. 디바운스 윈도우 안에 다른 Call 이 Going 들어오면 Ready 전이 취소.
    /// </summary>
    private void ScheduleFlowSync(Adapters.DspRepositoryAdapter repo, string flowName, bool enteringGoing, bool leavingGoing)
    {
        if (enteringGoing)
        {
            // 보류 중인 Ready 디바운스 취소 + 즉시 sync (결과는 Going)
            lock (_flowReadyLock)
            {
                if (_flowReadyDebounceCts.Remove(flowName, out var existing))
                {
                    try { existing.Cancel(); existing.Dispose(); } catch { /* ignore */ }
                }
            }
            _ = repo.SyncFlowStateAsync(flowName);
            return;
        }

        if (!leavingGoing)
        {
            // Going 무관 전이 (예: Finish→Ready) — flow.state 변할 일 없음, sync 생략
            return;
        }

        // leavingGoing — 디바운스 후 sync. 윈도우 안에 새 Going 들어오면 enteringGoing 분기에서 cancel.
        CancellationTokenSource cts;
        lock (_flowReadyLock)
        {
            if (_flowReadyDebounceCts.Remove(flowName, out var existing))
            {
                try { existing.Cancel(); existing.Dispose(); } catch { /* ignore */ }
            }
            cts = new CancellationTokenSource();
            _flowReadyDebounceCts[flowName] = cts;
        }
        var token = cts.Token;
        _ = Task.Delay(FlowReadyDebounceMs, token).ContinueWith(async _ =>
        {
            if (token.IsCancellationRequested) return;
            try { await repo.SyncFlowStateAsync(flowName); }
            catch (Exception ex) { _logger.LogWarning(ex, "[Engine] Deferred flow sync failed for {Flow}", flowName); }

            lock (_flowReadyLock)
            {
                if (_flowReadyDebounceCts.TryGetValue(flowName, out var c) && c == cts)
                {
                    _flowReadyDebounceCts.Remove(flowName);
                }
            }
            try { cts.Dispose(); } catch { /* ignore */ }
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// 래치 적격 Flow 의 dspFlow.state 를 head-start→tail-complete 엣지 래치에서 직접 도출해 쓴다(going-any 미사용).
    /// <list type="bullet">
    /// <item>"Going": DB="Going" + 스냅샷 즉시 "Going"(이전 Finish-hold 해제, holdMs=0). Body 구간 내내 유지.</item>
    /// <item>"Finish": 스냅샷에 "Finish" 를 <see cref="FlowLatchBadge.FinishHoldMs"/> 동안 보장 표시, DB="Ready"
    ///   → hold 만료 후 폴링이 "Ready" 로 정착(기존 Tail-Finish hold 와 동일 메커니즘).</item>
    /// <item>"Ready": DB="Ready" + 스냅샷 "Ready".</item>
    /// </list>
    /// 적격인데 상태 미추적(예외)이면 going-any 로 안전 폴백.
    /// </summary>
    private async Task WriteLatchBadgeAsync(Adapters.DspRepositoryAdapter repo, string flowName)
    {
        var badge = _flowMetricsService.GetLatchBadgeState(flowName);
        if (badge is null)
        {
            await repo.SyncFlowStateAsync(flowName);
            return;
        }

        switch (badge)
        {
            case FlowLatchBadge.Going:
                await repo.UpdateFlowStateAsync(flowName, FlowLatchBadge.Going);
                _dspDbService.SetFlowStateWithHold(flowName, FlowLatchBadge.Going, 0);
                break;

            case FlowLatchBadge.Finish:
                await repo.UpdateFlowStateAsync(flowName, FlowLatchBadge.Ready);
                _dspDbService.SetFlowStateWithHold(flowName, FlowLatchBadge.Finish, FlowLatchBadge.FinishHoldMs);
                break;

            default: // Ready
                await repo.UpdateFlowStateAsync(flowName, FlowLatchBadge.Ready);
                _dspDbService.SetFlowStateWithHold(flowName, FlowLatchBadge.Ready, 0);
                break;
        }
    }

    private sealed class CallStatsSeedRow
    {
        public Guid CallId { get; set; }
        public int GoingCount { get; set; }
        public double AverageGoingTime { get; set; }
        public double StdDevGoingTime { get; set; }
    }

    private void ApplySingleEffect(RuntimeHubEffect effect)
    {
        if (_engine is null) return;

        switch (effect.Kind)
        {
            case RuntimeHubEffectKind.Log:
                _logger.LogInformation("[Engine] {Severity}: {Msg}", effect.Severity, effect.Message);
                break;

            case RuntimeHubEffectKind.InjectIoByAddress:
                _engine.InjectIOValueByAddress(effect.Address, effect.Value);
                break;

            case RuntimeHubEffectKind.ForceWorkState:
                if (effect.WorkGuid != Guid.Empty)
                    _engine.ForceWorkState(effect.WorkGuid, effect.State);
                break;

            case RuntimeHubEffectKind.ForceWorkStateIfGoing:
                if (effect.WorkGuid != Guid.Empty)
                    _engine.TryForceWorkStateIfGoing(effect.WorkGuid, effect.State);
                break;

            case RuntimeHubEffectKind.ForceWorkStateIfReady:
                if (effect.WorkGuid != Guid.Empty)
                    _engine.TryForceWorkStateIfReady(effect.WorkGuid, effect.State);
                break;

            case RuntimeHubEffectKind.PassiveObserve:
                ObserveAndInferPassiveState(effect.Address, effect.Value);
                break;

            // WriteTag — DsPilot은 모니터 전용이므로 Hub로 다시 쓰지 않음 (스킵)
        }
    }

    private void ObserveAndInferPassiveState(string address, string value)
    {
        if (_engine is null || _passiveInference is null) return;

        // v12 P4 — abnormal timing 판정(cycle 학습과 독립): OutTag On=going / InTag On=finish.
        _monitoringAbnormal?.OnObservedIo(address, value, Environment.TickCount);

        var actions = _passiveInference.Observe(
            address, value,
            new Func<Guid, Status4>(GetWorkStateSafe),
            new Func<Guid, Status4>(GetCallStateSafe));

        foreach (var action in actions)
        {
            switch (action.TargetKind)
            {
                case PassiveInferenceTarget.Work:
                    if (!IsMappedDeviceWork(action.TargetGuid) && GetWorkStateSafe(action.TargetGuid) != action.State)
                        _engine.ForceWorkState(action.TargetGuid, action.State);
                    break;
                case PassiveInferenceTarget.Call:
                    if (GetCallStateSafe(action.TargetGuid) != action.State)
                        _engine.ForceCallState(action.TargetGuid, action.State);
                    break;
            }
        }

        foreach (var log in _passiveInference.DrainLogs())
            _logger.LogDebug("[Engine] passive: {Msg}", log.Message);
    }

    /// <summary>
    /// 이상감지 timeout 워치독 틱 — MonitoringAbnormalAdapter 제거 후 no-op 유지(StateReconcileService 호출부 무변경).
    public void TickAbnormalWatchdog() { }

    /// <summary>
    /// Phase 2 — 래치 적격 Flow 의 박제(stuck-open) 사이클 워치독. StateReconcileService 가 매 tick 호출한다.
    /// 열린 래치의 경과가 해당 flow 의 유효 이상치 Max(전체+flow별, 사후 IsIdle 분류와 동일 소스
    /// <see cref="AppSettingsService.ResolveEffectiveCycleRangeMs"/>)를 넘으면 — 지금 완료돼도 비가동으로
    /// 분류될 사이클이므로 — 래치를 abandon(사이클/통계 미기록)하고 "Ready" 로 복귀시킨다(라인 정지로 tail 미도달 시
    /// 박제 해소). effective Max=0(제한 없음)인 flow 는 해제하지 않는다(기존 Going-고정 해제 규칙과 동일).
    /// </summary>
    public async Task TickFlowLatchWatchdogAsync()
    {
        if (_engine is null) return;
        if (!_flowMetricsService.IsInitialized) return;
        if (_dspRepository is not Adapters.DspRepositoryAdapter repo) return;

        var active = _flowMetricsService.GetActiveLatchedCycles();
        if (active.Count == 0) return;

        var settings = _settings.LoadSettings();
        var now = DateTime.Now;

        foreach (var (flowName, cycleStart) in active)
        {
            var maxMs = AppSettingsService.ResolveEffectiveCycleRangeMs(settings, flowName).MaxMs;
            if (!FlowLatchBadge.ShouldAbandon(true, cycleStart, maxMs, now)) continue;

            if (_flowMetricsService.AbandonLatchedCycle(flowName))
            {
                try { await repo.UpdateFlowStateAsync(flowName, FlowLatchBadge.Ready); }
                catch (Exception ex) { _logger.LogWarning(ex, "[Engine] latch watchdog: Flow {Flow} Ready 쓰기 실패", flowName); }
                _dspDbService.SetFlowStateWithHold(flowName, FlowLatchBadge.Ready, 0);
                _logger.LogInformation(
                    "[Engine] latch watchdog: Flow {Flow} 사이클 경과 {Elapsed:F0}ms > 이상치 Max {Max}ms → abandon + Ready(미기록)",
                    flowName, (now - cycleStart).TotalMilliseconds, maxMs);
            }
        }
    }

    /// <summary>
    /// 통신 blackout — PLC 단절 전이 시 HubSubscriberService 가 호출. 진행 중이던 모든 래치 사이클을
    /// 즉시 abandon(사이클/통계 미기록)한다. 단절 구간은 신호 순서/edge 신뢰가 없어, 그 사이클이
    /// 나중에 완료돼도 head/tail 이 오염된 값이라 평균 CT·히스토리·클린사이클(자동보정)에 넣으면 안 된다.
    /// 범위는 전체 flow — PLC 어댑터→flow 매핑이 없고 현장 구성이 대부분 PLC 1대(부분화는 후속).
    /// latch watchdog(<see cref="TickFlowLatchWatchdogAsync"/>)의 abandon 경로와 동일 처리.
    /// </summary>
    public async Task AbandonActiveCyclesOnPlcBlackoutAsync(string adapterName, string lastError)
    {
        if (_engine is null) return;
        if (!_flowMetricsService.IsInitialized) return;
        if (_dspRepository is not Adapters.DspRepositoryAdapter repo) return;

        var active = _flowMetricsService.GetActiveLatchedCycles();
        if (active.Count == 0) return;

        foreach (var (flowName, cycleStart) in active)
        {
            if (_flowMetricsService.AbandonLatchedCycle(flowName))
            {
                try { await repo.UpdateFlowStateAsync(flowName, FlowLatchBadge.Ready); }
                catch (Exception ex) { _logger.LogWarning(ex, "[Engine] PLC blackout: Flow {Flow} Ready 쓰기 실패", flowName); }
                _dspDbService.SetFlowStateWithHold(flowName, FlowLatchBadge.Ready, 0);
                _logger.LogWarning(
                    "[Engine] PLC blackout ({Adapter}: {Err}): Flow {Flow} 진행 사이클(시작 {Start:HH:mm:ss}) abandon + Ready(미기록)",
                    adapterName, lastError, flowName, cycleStart);
            }
        }
    }

    /// <summary>
    /// Agent "OnAbnormal" SignalR 핸들러 진입점.
    /// Agent ControlAbnormalAdapter 가 실제 Going/Ready 상태 기반으로 정밀 감지한 결과를
    /// AbnormalEventService 로 직접 전달 — 로컬 MonitoringAbnormalAdapter 를 대체한다.
    /// </summary>
    public void HandleHubAbnormal(AbnormalPayload payload)
    {
        if (_abnormalEvents is null) return;
        try
        {
            var kind = (AbnormalKind)payload.KindValue;
            var target = Abnormal.target(
                ParseFsGuid(payload.CallId),
                ParseFsGuid(payload.ApiCallId),
                ParseFsGuid(payload.WorkId));
            var record = kind switch
            {
                AbnormalKind.SensorOpen  => Abnormal.sensorOpen(target, payload.TimestampUtc),
                AbnormalKind.SensorShort => Abnormal.sensorShort(target, payload.TimestampUtc),
                AbnormalKind.ActionOver  => Abnormal.actionOver(target, payload.ElapsedMs, payload.TimestampUtc),
                AbnormalKind.ActionUnder => Abnormal.actionUnder(target, payload.ElapsedMs, payload.TimestampUtc),
                _ => Abnormal.sensorShort(target, payload.TimestampUtc)
            };
            _abnormalEvents.Record(record);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Abnormal] Agent payload 처리 실패 (kind={Kind})", payload.KindValue);
        }
    }

    private static FSharpOption<Guid> ParseFsGuid(string s)
        => Guid.TryParse(s, out var g) ? FSharpOption<Guid>.Some(g) : FSharpOption<Guid>.None;

    private Status4 GetWorkStateSafe(Guid g)
    {
        if (_engine is null) return Status4.Ready;
        var opt = _engine.GetWorkState(g);
        return opt != null && FSharpOption<Status4>.get_IsSome(opt) ? opt.Value : Status4.Ready;
    }

    private bool IsMappedDeviceWork(Guid workGuid)
    {
        var engine = _engine;
        return engine is not null
               && (engine.IOMap.TxWorkToOutAddresses.Any(kv => kv.Key == workGuid)
                   || engine.IOMap.RxWorkToInAddresses.Any(kv => kv.Key == workGuid));
    }

    private Status4 GetCallStateSafe(Guid g)
    {
        if (_engine is null) return Status4.Ready;
        var opt = _engine.GetCallState(g);
        return opt != null && FSharpOption<Status4>.get_IsSome(opt) ? opt.Value : Status4.Ready;
    }

    /// <summary>
    /// 라이브 상태 self-heal — 엔진 in-memory Call 상태(정본)와 DB(dspCall/dspFlow)를 대조해,
    /// 쓰기 유실/지연(예: 자동 재계산의 무거운 트랜잭션과 락 경합)으로 'Going'에 latch된 행을 교정한다.
    /// <para>보수적: <b>DB=Going 인데 엔진=non-Going</b> 인 발산만 교정한다. 엔진도 Going 이면 정상 진행 중이므로
    /// 건드리지 않고(=절대 Going 으로 강제하지 않음), 깜빡임/이벤트 경로와 충돌하지 않는다.
    /// 엔진 자체가 Going 에 멈춘 경우(완료 신호 미수신)는 발산이 아니므로 별도 'Going 고정 해제'로 다룬다.</para>
    /// <para><b>Going 고정 해제</b>: 엔진도 Going(=발산 아님)이지만 경과가 해당 flow 의 유효 이상치 Max
    /// (<see cref="AppSettingsService.ResolveEffectiveCycleRangeMs"/> — 전체 + flow별 비가동 임계, 사후 IsIdle
    /// 분류와 동일 소스)를 넘으면, 지금 완료돼도 어차피 비가동으로 분류될 사이클이므로 'Going 고정'으로 보고
    /// Ready 로 강제 복귀시킨다(통계/사이클 미기록). effective Max=0(제한 없음)인 flow 는 해제하지 않는다.</para>
    /// </summary>
    /// <returns>교정한 Call 수(발산 self-heal + Going 고정 해제).</returns>
    public async Task<int> ReconcileStuckStatesAsync()
    {
        if (_engine is null) return 0;
        if (_dspRepository is not Adapters.DspRepositoryAdapter repo) return 0;

        List<Adapters.GoingCallInfo> goingCalls;
        try { goingCalls = await repo.GetGoingCallsAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "[Engine] reconcile: Going Call 조회 실패"); return 0; }
        if (goingCalls.Count == 0) return 0;

        // flow별 유효 이상치 Max(전체+flow별, 사후 비가동 분류와 동일 소스)를 'Going 고정' 해제 임계로 재사용.
        // 디스크 재로드를 줄이기 위해 tick 당 한 번만 로드.
        var settings = _settings.LoadSettings();

        var affectedFlows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 엔진 Going·이상치 Max 미초과(=정상 진행)인 Call 을 가진 flow — Phase 3 head-start 엣지 유실 교차검증 후보.
        var flowsWithHealthyGoing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.Now;
        int corrected = 0;
        int timedOut = 0;

        foreach (var gc in goingCalls)
        {
            if (gc.CallId == Guid.Empty) continue;

            if (GetCallStateSafe(gc.CallId) == Status4.Going)
            {
                // 엔진도 Going → 발산 아님(정상 진행 중). 단, 경과가 flow 유효 이상치 Max 를 넘으면
                // Going 고정으로 보고 Ready 강제 복귀. ForceCallState 가 Going→Ready 이벤트를 발사 →
                // HandleCallStateChangeAsync 의 abandon 경로가 DB 쓰기/flow 동기화/통지를 처리(통계·사이클
                // 미기록). 여기선 엔진만 건드린다.
                var maxMs = AppSettingsService.ResolveEffectiveCycleRangeMs(settings, gc.FlowName).MaxMs;
                if (maxMs > 0 && IsGoingStuckBeyond(gc.CallId, maxMs, now))
                {
                    MarkGoingAbandoned(gc.CallId);
                    _engine.ForceCallState(gc.CallId, Status4.Ready);
                    timedOut++;
                    _logger.LogInformation(
                        "[Engine] reconcile: Going 고정 해제 — Call {Call}(flow {Flow}) 경과 > 이상치 Max {Max}ms → Ready 복귀(미기록)",
                        gc.CallName, gc.FlowName, maxMs);
                }
                else if (!string.IsNullOrEmpty(gc.FlowName))
                {
                    // 정상 진행 중인 Going Call(stuck 아님) — 래치 닫힘이면 head-start 엣지 유실 의심. Phase 3 에서 판정.
                    flowsWithHealthyGoing.Add(gc.FlowName);
                }
                continue;
            }

            var next = MapStatus4(GetCallStateSafe(gc.CallId)); // 엔진 정본 상태로 교정
            bool ok;
            try { ok = await _dspRepository.UpdateCallStateAsync(gc.CallId, next); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Engine] reconcile: Call {Call} 교정 실패", gc.CallName);
                continue;
            }
            if (!ok) continue;

            corrected++;
            if (!string.IsNullOrEmpty(gc.FlowName)) affectedFlows.Add(gc.FlowName);
            _notificationService.NotifyStateChanged(gc.CallName, "Going", next, now);
        }

        // Phase 3 — head-start 엣지 유실 교차검증(래치 적격 한정 자가치유). 적격 flow 가 래치 닫힘인데 정상
        // Going Call 이 grace(LatchEdgeLossGraceMs) 이상 지속되면 head-start 누락으로 보고 래치를 연다.
        // stuck(이상치 Max 초과) Call 은 위에서 제외했으므로 워치독 abandon 과 플랩하지 않는다.
        foreach (var flow in flowsWithHealthyGoing)
        {
            if (!_flowMetricsService.IsLatchEligible(flow) || _flowMetricsService.IsLatchCycleActive(flow))
            {
                _latchEdgeLossSince.Remove(flow);
                continue;
            }
            if (!_latchEdgeLossSince.TryGetValue(flow, out var since))
            {
                _latchEdgeLossSince[flow] = now;
                continue;
            }
            if ((now - since).TotalMilliseconds >= LatchEdgeLossGraceMs)
            {
                if (_flowMetricsService.TryForceOpenLatch(flow, since))
                {
                    try { await repo.UpdateFlowStateAsync(flow, FlowLatchBadge.Going); }
                    catch (Exception ex) { _logger.LogWarning(ex, "[Engine] reconcile: latch 교차검증 Flow {Flow} Going 쓰기 실패", flow); }
                    _dspDbService.SetFlowStateWithHold(flow, FlowLatchBadge.Going, 0);
                    _logger.LogInformation(
                        "[Engine] reconcile: latch 교차검증 — Flow {Flow} 정상 Going {Elapsed:F0}ms·래치 닫힘 → head-start 엣지 유실 추정, 래치 open",
                        flow, (now - since).TotalMilliseconds);
                }
                _latchEdgeLossSince.Remove(flow);
            }
        }
        // grace 타이머 정리 — 이번 tick 에 정상 Going 후보가 아닌 flow 는 리셋(지속성 추적).
        if (_latchEdgeLossSince.Count > 0)
        {
            foreach (var k in _latchEdgeLossSince.Keys.Where(k => !flowsWithHealthyGoing.Contains(k)).ToList())
                _latchEdgeLossSince.Remove(k);
        }

        // 발산 교정으로 영향받은 flow 의 dspFlow.state 재동기화 — 적격 flow 는 래치 기반 배지(going-any 가
        // Body 구간 Going 을 덮어쓰지 않게), 미적격 flow 는 기존 going-any.
        foreach (var flow in affectedFlows)
        {
            try
            {
                if (_flowMetricsService.IsLatchEligible(flow))
                    await WriteLatchBadgeAsync(repo, flow);
                else
                    await repo.SyncFlowStateAsync(flow);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "[Engine] reconcile: Flow {Flow} sync 실패", flow); }
        }

        if (corrected > 0)
            _logger.LogInformation(
                "[Engine] reconcile: {Count}개 stale-Going Call 교정(engine↔DB 발산) — flows={Flows}",
                corrected, string.Join(",", affectedFlows));
        return corrected + timedOut;
    }

    /// <summary>
    /// 엔진이 Going 인 Call 의 경과시간이 ceilingMs(= flow 유효 이상치 Max)를 넘었는지. 넘었으면 지금
    /// 완료돼도 비가동으로 분류될 사이클이므로 'Going 고정'으로 보고 해제 대상. 이 프로세스에서 Going
    /// 시작 시각을 모르면(재시작 등) 판단 보류(false).
    /// </summary>
    private bool IsGoingStuckBeyond(Guid callGuid, int ceilingMs, DateTime now)
    {
        DateTime startedAt;
        lock (_statsLock)
        {
            if (!_callStats.TryGetValue(callGuid, out var s) || s.startedAt == default)
                return false;
            startedAt = s.startedAt;
        }
        return (now - startedAt).TotalMilliseconds > ceilingMs;
    }

    /// <summary>Going 고정 해제 표시 + Going 시작 시각 리셋(통계 오염/재진입 방지).</summary>
    private void MarkGoingAbandoned(Guid callGuid)
    {
        lock (_statsLock)
        {
            _timeoutAbandoned.Add(callGuid);
            if (_callStats.TryGetValue(callGuid, out var s))
                _callStats[callGuid] = (default, s.count, s.mean, s.m2);
        }
    }

    // ===== Engine state events =====

    private void OnEngineWorkStateChanged(object? sender, WorkStateChangedArgs args)
    {
        _logger.LogDebug("[Engine] Work {Name}: {Prev} → {New}",
            args.WorkName, args.PreviousState, args.NewState);
    }

    private void OnEngineCallStateChanged(object? sender, CallStateChangedArgs args)
    {
        // 엔진은 동기 컨텍스트에서 이벤트 발사 — 채널에 enqueue 만 하고 즉시 반환.
        // 실제 DB 작업은 단일 컨슈머 (ConsumeEngineEventsAsync) 가 순차 처리.
        var ch = _eventChannel;
        if (ch is null) return;
        if (!ch.Writer.TryWrite(args))
            _logger.LogWarning("[Engine] Event channel write dropped for {Call}", args.CallName);
    }

    private async Task ConsumeEngineEventsAsync(Channel<CallStateChangedArgs> channel, CancellationToken ct)
    {
        try
        {
            await foreach (var args in channel.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await HandleCallStateChangeAsync(args);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Engine] Call state handler failed for {Call}", args.CallName);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 정상 종료
        }
    }

    private async Task HandleCallStateChangeAsync(CallStateChangedArgs args)
    {
        var prev = MapStatus4(args.PreviousState);
        var next = MapStatus4(args.NewState);
        var now = DateTime.Now;
        var callGuid = args.CallGuid;
        var callName = args.CallName;

        _logger.LogDebug(
            "[Engine] Call {Call}: {Prev} → {New} (skipped={Skipped})",
            callName, prev, next, args.IsSkipped);

        // 1. dspCall DB UPDATE.
        //    Going 진입 → start tracking. Going 이탈 (어떤 상태로든) → stop tracking + 통계 갱신.
        //    OutOnly Direction 은 엔진이 Going→Ready 직접 전이할 수 있어, Going→Finish 만
        //    처리하면 통계가 누락된다 — Going 이탈 자체를 finish 로 본다.
        var enteringGoing = args.PreviousState != Status4.Going && args.NewState == Status4.Going;
        var leavingGoing  = args.PreviousState == Status4.Going && args.NewState != Status4.Going;

        // Going 고정 해제(reconcile)로 강제된 Going 이탈은 정상 완료가 아니다 — finish 통계/사이클로 집계하지 않는다.
        bool abandoned = false;
        if (leavingGoing)
            lock (_statsLock) { abandoned = _timeoutAbandoned.Remove(callGuid); }
        bool finishingGoing = leavingGoing && !abandoned;

        // DB write 실패는 in-memory snapshot 과 divergence 를 만들 수 있으므로 명시적 Warn 로그.
        // (이후 1초 폴링이 snapshot 을 DB 값으로 덮어쓰면서 UI 깜빡임 가능 — 원인 추적 용이하게 기록.)
        bool dbOk;
        if (enteringGoing)
        {
            RecordGoingStart(callGuid, now);
            dbOk = await _dspRepository.UpdateCallStateAsync(callGuid, next);
        }
        else if (finishingGoing)
        {
            // 동작편차 통계는 히스토리 재구성과 동일 캡으로 이상치를 제외하고 누산. 캡 밖(분 단위로 늘어진
            // Going 등)이면 통계는 미반영하고 상태만 전이 — flow 사이클 hook(아래)은 finishingGoing 그대로 유지.
            var (minMs, maxMs) = GetGoingTimeCaps();
            var (recorded, durMs, avg, stdDev) = RecordGoingFinish(callGuid, now, minMs, maxMs);
            dbOk = recorded
                ? await _dspRepository.UpdateCallWithStatisticsAsync(callGuid, next, durMs, avg, stdDev)
                : await _dspRepository.UpdateCallStateAsync(callGuid, next);
        }
        else
        {
            dbOk = await _dspRepository.UpdateCallStateAsync(callGuid, next);
        }

        if (!dbOk)
        {
            _logger.LogWarning(
                "[Engine] dspCall DB write failed: Call={Call} ({Prev}→{New}) — snapshot may diverge until next poll",
                callName, prev, next);
        }

        // 2. Flow 이름 조회.
        var info = await _dspRepository.GetCallInfoAsync(callGuid);
        var flowName = info?.FlowName ?? string.Empty;

        // 3. FlowMetrics 사이클 hook — dspFlow.state 동기화(4)보다 *먼저* 래치를 갱신해야 래치 기반 배지를 읽을 수 있다.
        //    Going 진입/이탈 기준 — OutOnly 의 Going→Ready 도 finish 로 처리. abandon 은 사이클 미기록.
        //    (AASX 로딩 실패로 미초기화 상태일 때 NRE 방지.) 메트릭(MT/WT/CT)은 적격 여부와 무관하게 래치로 추적.
        if (!string.IsNullOrEmpty(flowName) && _flowMetricsService.IsInitialized)
        {
            if (enteringGoing)
                _flowMetricsService.OnCallGoingStarted(flowName, callName, now);
            else if (finishingGoing)
                _flowMetricsService.OnCallFinished(flowName, callName, now);
        }

        // 4. dspFlow.state 동기화.
        if (!string.IsNullOrEmpty(flowName) && _dspRepository is Adapters.DspRepositoryAdapter repo)
        {
            if (_flowMetricsService.IsLatchEligible(flowName))
            {
                // 래치 적격(단일 head/tail): head-start→tail-complete 엣지 래치에서 배지를 직접 도출한다.
                // Body 구간(어느 Call 도 Going 이 아닌 순간)에도 사이클이 열려 있어 "가동중"이 유지된다(깜빡임 없음).
                await WriteLatchBadgeAsync(repo, flowName);
            }
            else
            {
                // 미적격(미정의·복수 head/tail·동명 모호): 기존 going-any 폴백 + Tail-Finish hold (회귀 0).
                // Tail Call 의 Finish 가 사이클 1회 종료 — dspFlow.state="Finish" 를 250ms hold 로 표시
                //   실 PLC: cycle 사이의 자연스러운 idle gap 동안 폴링이 잡음 / 시뮬: gap≈0ms 라 hold 로 강제 표시
                if (finishingGoing && _flowMetricsService.IsInitialized)
                {
                    var (_, tailCallName) = _flowMetricsService.GetCycleBoundaryCallNames(flowName);
                    if (!string.IsNullOrEmpty(tailCallName) && tailCallName == callName)
                    {
                        _ = repo.UpdateFlowStateAsync(flowName, "Finish");
                        _dspDbService.SetFlowStateWithHold(flowName, "Finish", 250);
                    }
                }
                // abandon(타임아웃 해제)도 Going 이탈이므로 flow 는 Ready 로 내려야 한다 → leavingGoing 그대로 전달.
                ScheduleFlowSync(repo, flowName, enteringGoing, leavingGoing);
            }
        }

        // 4. DspDbService EventWriter — 1초 폴링 대기 없이 UI 즉시 반영
        var snap = await _dspRepository.GetCallByIdAsync(callGuid);
        var evt = new CallStateChangedEvent
        {
            CallName = callName,
            PreviousState = prev,
            NewState = next,
            GoingCount = snap?.GoingCount ?? 0,
            AverageGoingTime = snap?.AverageGoingTime,
            PreviousGoingTime = snap?.PreviousGoingTime,
            Timestamp = now,
        };
        _dspDbService.EventWriter.TryWrite(evt);

        // 5. SignalR broadcast (CallStateNotificationService → MonitoringBroadcastService)
        _notificationService.NotifyStateChanged(callName, prev, next, now);
    }

    // ===== Welford stats =====

    private void RecordGoingStart(Guid callGuid, DateTime now)
    {
        lock (_statsLock)
        {
            if (!_callStats.TryGetValue(callGuid, out var s))
                s = (now, 0, 0, 0);
            else
                s = (now, s.count, s.mean, s.m2);
            _callStats[callGuid] = s;
        }
    }

    /// <summary>
    /// Going 종료 시 경과시간을 Welford 누적기에 반영. <paramref name="minMs"/>/<paramref name="maxMs"/>
    /// (0=해당 방향 제한 없음) 캡을 벗어난 이상치는 누산하지 않고 <c>recorded=false</c> 를 반환 —
    /// 히스토리 재구성(ComputeExecutionRecordsAsync)과 동일한 필터를 라이브 통계에도 적용해
    /// 매트릭스/상세 패널 편차가 어긋나지 않게 한다. 어느 경우든 startedAt 은 클리어(다음 사이클 깨끗하게).
    /// </summary>
    private (bool recorded, int durMs, double mean, double stdDev) RecordGoingFinish(
        Guid callGuid, DateTime now, int minMs, int maxMs)
    {
        lock (_statsLock)
        {
            if (!_callStats.TryGetValue(callGuid, out var s) || s.startedAt == default)
                return (false, 0, 0, 0);

            var durMs = (now - s.startedAt).TotalMilliseconds;

            bool inRange = durMs > 0
                && (maxMs <= 0 || durMs <= maxMs)
                && (minMs <= 0 || durMs >= minMs);
            if (!inRange)
            {
                // 통계는 보존하고 startedAt 만 클리어 — 이상치 표본을 누적 평균/표준편차에서 제외.
                _callStats[callGuid] = (default, s.count, s.mean, s.m2);
                return (false, 0, 0, 0);
            }

            var newCount = s.count + 1;
            var delta = durMs - s.mean;
            var newMean = s.mean + delta / newCount;
            var delta2 = durMs - newMean;
            var newM2 = s.m2 + delta * delta2;
            var newStdDev = newCount > 1 ? Math.Sqrt(newM2 / newCount) : 0.0;

            _callStats[callGuid] = (default, newCount, newMean, newM2);
            return (true, (int)Math.Round(durMs), newMean, newStdDev);
        }
    }

    /// <summary>
    /// 동작편차 통계 캡(min/max ms)을 5초 TTL 캐시로 반환 — finish 마다 설정 디스크 재로드 방지.
    /// </summary>
    private (int minMs, int maxMs) GetGoingTimeCaps()
    {
        lock (_goingCapsLock)
        {
            var nowTick = Environment.TickCount64;
            if (nowTick - _goingCapsLoadedTick > 5000)
            {
                try
                {
                    var hv = _settings.LoadSettings().HistoryView;
                    _maxCallGoingMs = hv.MaxCallGoingTimeMs;
                    _minCallGoingMs = hv.MinCallGoingTimeMs;
                }
                catch { /* 설정 읽기 실패 시 직전 캐시 유지 */ }
                _goingCapsLoadedTick = nowTick;
            }
            return (_minCallGoingMs, _maxCallGoingMs);
        }
    }

    /// <summary>
    /// dspCall 의 누적 통계를 다시 읽어 Welford 누적기를 재시드한다(외부 self-heal 재계산 직후 호출).
    /// 기존 in-memory 통계를 비우고 DB 정본으로 교체 — 캡 재도출로 0 이 된 Call 은 누적기에서 제거된다.
    /// </summary>
    public void ReseedCallStatsFromDb()
    {
        lock (_statsLock) { _callStats.Clear(); }
        SeedCallStatsFromDb();
    }

    private static string MapStatus4(Status4 s) => s switch
    {
        Status4.Ready => "Ready",
        Status4.Going => "Going",
        Status4.Finish => "Finish",
        Status4.Homing => "Homing",
        _ => "Ready",
    };

    /// <summary>
    /// 엔진/컨슈머/캐시 전체 teardown. 호출 후 다음 TryEnsureInitialized() 호출 시 fresh 상태로 재시작.
    /// 사용 시나리오: plc.db 삭제 후 재로딩, AASX 변경 후 재초기화 등.
    /// </summary>
    public async Task ResetAsync()
    {
        // 1. 새 이벤트 차단
        try { _eventChannel?.Writer.TryComplete(); } catch { /* ignore */ }
        try { _consumerCts?.Cancel(); } catch { /* ignore */ }

        // 2. 엔진 정지 + dispose
        if (_engine is not null)
        {
            try
            {
                _engine.CallStateChanged -= OnEngineCallStateChanged;
                _engine.WorkStateChanged -= OnEngineWorkStateChanged;
                try { _engine.Stop(); } catch { /* already stopped */ }
                _engine.Dispose();
            }
            catch { /* ignore */ }
            _engine = null;
        }

        // 3. 잔여 큐 처리 + 컨슈머 종료 대기
        if (_consumerTask is not null)
        {
            try { await _consumerTask.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { /* timeout 또는 cancel 정상 */ }
        }
        try { _consumerCts?.Dispose(); } catch { /* ignore */ }

        // 4. 모든 in-memory 상태 클리어
        lock (_initLock)
        {
            _eventChannel = null;
            _consumerCts = null;
            _consumerTask = null;
            _runtimeSession = null;
            _passiveInference = null;
            _initFailed = false;
        }
        lock (_statsLock) { _callStats.Clear(); _timeoutAbandoned.Clear(); }
        _plcTagIdByAddress.Clear();

        // 보류 중인 flow ready 디바운스 모두 취소
        lock (_flowReadyLock)
        {
            foreach (var c in _flowReadyDebounceCts.Values)
            {
                try { c.Cancel(); c.Dispose(); } catch { /* ignore */ }
            }
            _flowReadyDebounceCts.Clear();
        }

        _logger.LogInformation("[Engine] Reset complete — ready for re-initialization");
    }

    /// <summary>
    /// Welford 누적기만 reset (엔진/세션은 유지). Flow 히스토리 클리어 시나리오용.
    /// </summary>
    public void ResetCallStats()
    {
        lock (_statsLock) { _callStats.Clear(); }
        _logger.LogInformation("[Engine] In-memory call stats cleared");
    }

    public void Dispose()
    {
        // 동기 dispose — ResetAsync 결과 기다리지 않고 fast teardown
        try { _eventChannel?.Writer.TryComplete(); } catch { /* ignore */ }
        try { _consumerCts?.Cancel(); } catch { /* ignore */ }

        if (_engine is not null)
        {
            try
            {
                _engine.CallStateChanged -= OnEngineCallStateChanged;
                _engine.WorkStateChanged -= OnEngineWorkStateChanged;
                try { _engine.Stop(); } catch { /* ignore */ }
                _engine.Dispose();
            }
            catch { /* ignore */ }
            _engine = null;
        }

        try { _consumerTask?.Wait(2000); } catch { /* ignore */ }
        try { _consumerCts?.Dispose(); } catch { /* ignore */ }

        _runtimeSession = null;
        _passiveInference = null;
    }
}
