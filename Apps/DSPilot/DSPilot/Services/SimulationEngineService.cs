using System.Threading.Channels;
using Dapper;
using DSPilot.Infrastructure;
using DSPilot.Models;
using DSPilot.Repositories;
using Ds2.Core;
using Ds2.Runtime.Engine;
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
    private readonly ILogger<SimulationEngineService> _logger;

    private ISimulationEngine? _engine;
    private RuntimeModeSession? _runtimeSession;
    private PassiveInferenceSession? _passiveInference;
    private readonly object _initLock = new();
    private bool _initFailed;

    // Welford 통계 — Going 시작 시각 + 누적 (count, mean, M2)
    private readonly Dictionary<Guid, (DateTime startedAt, int count, double mean, double m2)> _callStats = new();
    private readonly object _statsLock = new();

    // 주소 → plcTag.id 캐시 (CycleTimeAnalysis 가 보는 plcTagLog INSERT 용)
    private readonly Dictionary<string, int> _plcTagIdByAddress = new(StringComparer.OrdinalIgnoreCase);

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
        ILogger<SimulationEngineService> logger)
    {
        _projectService = projectService;
        _dspRepository = dspRepository;
        _dspDbService = dspDbService;
        _flowMetricsService = flowMetricsService;
        _notificationService = notificationService;
        _pathResolver = pathResolver;
        _logWriter = logWriter;
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
                    passive = new PassiveInferenceSession(engine.Index, engine.IOMap, RuntimeMode.Monitoring);

                _engine = engine;
                _runtimeSession = runtimeSession;
                _passiveInference = passive;

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
        if (_plcTagIdByAddress.TryGetValue(address, out var tagId))
            _logWriter.TryWrite(tagId, value, DateTime.Now);

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
    /// DsStore 의 모든 IOTag (Out + In) 를 plcTag 테이블에 INSERT — 한 번만.
    /// 캐시 _plcTagIdByAddress 채우기.
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

            case RuntimeHubEffectKind.PassiveObserve:
                ObserveAndInferPassiveState(effect.Address, effect.Value);
                break;

            // WriteTag — DsPilot은 모니터 전용이므로 Hub로 다시 쓰지 않음 (스킵)
        }
    }

    private void ObserveAndInferPassiveState(string address, string value)
    {
        if (_engine is null || _passiveInference is null) return;

        var actions = _passiveInference.Observe(
            address, value,
            new Func<Guid, Status4>(GetWorkStateSafe),
            new Func<Guid, Status4>(GetCallStateSafe));

        foreach (var action in actions)
        {
            switch (action.TargetKind)
            {
                case PassiveInferenceTarget.Work:
                    if (GetWorkStateSafe(action.TargetGuid) != action.State)
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

    private Status4 GetWorkStateSafe(Guid g)
    {
        if (_engine is null) return Status4.Ready;
        var opt = _engine.GetWorkState(g);
        return opt != null && FSharpOption<Status4>.get_IsSome(opt) ? opt.Value : Status4.Ready;
    }

    private Status4 GetCallStateSafe(Guid g)
    {
        if (_engine is null) return Status4.Ready;
        var opt = _engine.GetCallState(g);
        return opt != null && FSharpOption<Status4>.get_IsSome(opt) ? opt.Value : Status4.Ready;
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

        if (enteringGoing)
        {
            RecordGoingStart(callGuid, now);
            await _dspRepository.UpdateCallStateAsync(callGuid, next);
        }
        else if (leavingGoing)
        {
            var (durMs, avg, stdDev) = RecordGoingFinish(callGuid, now);
            await _dspRepository.UpdateCallWithStatisticsAsync(
                callGuid, next, durMs, avg, stdDev);
        }
        else
        {
            await _dspRepository.UpdateCallStateAsync(callGuid, next);
        }

        // 2. Flow 이름 조회 + dspFlow.state 동기화 (Going Call 이 있으면 Going, 없으면 Ready)
        var info = await _dspRepository.GetCallInfoAsync(callGuid);
        var flowName = info?.FlowName ?? string.Empty;
        if (!string.IsNullOrEmpty(flowName) && _dspRepository is Adapters.DspRepositoryAdapter repo)
        {
            await repo.SyncFlowStateAsync(flowName);
        }

        // 3. FlowMetrics 사이클 hook (AASX 로딩 실패로 미초기화 상태일 때 NRE 방지)
        //    Going 진입/이탈 기준 — OutOnly 의 Going→Ready 도 finish 로 처리.
        if (!string.IsNullOrEmpty(flowName) && _flowMetricsService.IsInitialized)
        {
            if (enteringGoing)
                _flowMetricsService.OnCallGoingStarted(flowName, callName, now);
            else if (leavingGoing)
                _flowMetricsService.OnCallFinished(flowName, callName, now);
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

    private (int durMs, double mean, double stdDev) RecordGoingFinish(Guid callGuid, DateTime now)
    {
        lock (_statsLock)
        {
            if (!_callStats.TryGetValue(callGuid, out var s) || s.startedAt == default)
                return (0, 0, 0);

            var durMs = (now - s.startedAt).TotalMilliseconds;
            var newCount = s.count + 1;
            var delta = durMs - s.mean;
            var newMean = s.mean + delta / newCount;
            var delta2 = durMs - newMean;
            var newM2 = s.m2 + delta * delta2;
            var newStdDev = newCount > 1 ? Math.Sqrt(newM2 / newCount) : 0.0;

            _callStats[callGuid] = (default, newCount, newMean, newM2);
            return ((int)Math.Round(durMs), newMean, newStdDev);
        }
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
        lock (_statsLock) { _callStats.Clear(); }
        _plcTagIdByAddress.Clear();

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
