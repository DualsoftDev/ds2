// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Collections.Concurrent;
using DSPilot.Infrastructure;
using DSPilot.Models;
using DSPilot.Models.Analysis;
using DSPilot.Repositories;
using DSPilot.Services;

namespace DSPilot.Kpi;

/// <summary>
/// 완료된 사이클을 시간 기반 코어 DB 로 적재한다. doc/30 §2 · §3 · §4 · §13 3차.
/// <para>
/// 하는 일은 넷이다. ① 신호에서 call 구간(§2.2)을 만들고 ② 사이클마다 분기 제외 call 을 뺀 뒤 work 구간(§2.3)·
/// MT(§2.4)·경계 초과(§3)를 재고 ③ 그 시점의 기준선 R·W·MT중앙 과 게이트(§4.1)를 박제하고 ④ 한 트랜잭션으로
/// 저장한다. 상태는 저장하지 않는다 — 조회 시 현재 κ 로 도출한다.
/// </para>
/// <para>
/// 사이클 <b>출처</b> = 원시 신호(signal)의 PLC 태그 시각으로 도출한 경계(<see cref="CycleSourceDeriver"/>) — 간트·주기
/// 재도출과 같은 함수다(2026-10-02). 종전엔 라이브 기록(dspFlowHistory, 끝 = 처리 시각 UtcNow)을 받아 경계가 처리
/// 지연만큼(현장 0.2초~수십 초) 늦었고, 다음 사이클 head call 이 앞 사이클에 귀속돼 경계 초과 대량 제외·MT=CT 가짜
/// 비가동·MT중앙 오염·간트 뱃지 한 칸 밀림이 났다.
/// </para>
/// <para>
/// 처리 상한은 flow 의 <b>시스템(PLC)별</b> 최신 신호 시각 − 정착 여유. 전역 최신을 쓰면 늦게 들어오는 PLC 의 중간
/// 경계가 오기 전에 구간을 닫아 두 사이클이 하나로 합쳐진다. 통신 공백(미계측, 시스템별)과 겹치는 사이클은 경계를
/// 믿을 수 없으므로 <see cref="ExcludeReason.Unknown"/> 으로 박제한다. 미계측 조회가 실패하면 그 flow 는 이번 주기를
/// 미룬다.
/// </para>
/// <para>
/// <b>재적재</b>(2026-10-02): 사이클 표는 원시 신호에서 결정적으로 다시 만들 수 있는 캐시다. flow 의 원시 신호가 남은
/// 구간을 지우고 오래된 것부터 다시 적재하며, 기준선은 각 사이클 <b>시점의</b> 14일 창으로 다시 박제한다.
/// 트리거 = ① 기동 시 판정 규칙 버전(<see cref="KpiDb.SpecVersion"/>)이 다른 행 ② 경계·분기 저장 ③ 관리자 요청.
/// 원시 신호가 이미 지워진 구간의 행은 손대지 않는다. 적재·재적재·기준선 채움은 한 게이트로 직렬화한다.
/// </para>
/// </summary>
public sealed class CycleIngestService : BackgroundService
{
    /// <summary>적재 주기.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>기동 후 첫 적재까지의 여유 — 스키마 생성·엔진 초기화가 끝난 뒤 시작한다.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);

    /// <summary>한 번에 처리할 최대 사이클 수 — 신호 조회 구간이 지나치게 넓어지지 않게 한다.</summary>
    private const int BatchLimit = 400;

    /// <summary>한 번에 기준선을 뒤늦게 찍어 줄 최대 행 수.</summary>
    private const int BackfillLimit = 2000;

    /// <summary>call 신호를 조회할 때 사이클 앞뒤로 두는 여유(ms) — 경계에 걸친 OUT↑/IN↑ 짝을 놓치지 않기 위함.
    /// 경계 엣지 조회의 앞 여유로도 쓴다 — 엣지 판정(LAG)이 구간 첫 행의 직전 값을 알아야 가짜 상승을 안 만든다.</summary>
    private const long SignalPadMs = 60_000;

    /// <summary>정착 여유(ms) — 시스템 최신 신호보다 이만큼 앞선 경계까지만 닫는다. 끝 근처 call 의 하강·응답이
    /// 아직 안 들어와 MT·초과가 덜 재진 채 박제되는 것을 막는다.</summary>
    private const long SettleMs = 10_000;

    /// <summary>한 번에 도출하는 최대 구간(ms). 이 안에 닫힌 사이클이 없으면 상한까지 한 번 넓힌다(장기 정지).</summary>
    private const long MaxSpanMs = 6 * 3_600_000L;

    private readonly KpiRepository _repo;
    private readonly BaselineService _baselines;
    private readonly AppSettingsService _settings;
    private readonly IServiceScopeFactory _scopes;
    private readonly PlcToCallMapperService _mapper;
    private readonly CycleSourceDeriver _deriver;
    private readonly IFlowMetricsService _flowMetrics;
    private readonly DsProjectService _project;
    private readonly IPlcRepository _plc;
    private readonly OeeCommHealthService _commHealth;
    private readonly ILogger<CycleIngestService> _logger;

    /// <summary>시작 경계 미해석 경고를 이미 남긴 flow — 30초마다 같은 경고가 쌓이지 않게. 해석되면 지운다.</summary>
    private readonly HashSet<string> _warnedUnresolved = new(StringComparer.Ordinal);

    /// <summary>cycle 표 쓰기 직렬화 — 적재·재적재·기준선 채움이 서로의 워터마크·표본을 흔들지 않게.</summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>재적재 대기 flow. 컨트롤러(경계·분기 저장, 관리자 요청)가 넣고 적재 루프가 꺼낸다.</summary>
    private readonly ConcurrentDictionary<string, byte> _rebuildQueue = new(StringComparer.Ordinal);

    /// <summary>기동 후 규칙 버전 점검을 했는지 — 버전은 배포 때만 바뀌므로 한 번이면 된다.</summary>
    private bool _specChecked;

    private volatile KpiRebuildStatus _rebuildStatus = KpiRebuildStatus.Idle;

    /// <summary>이번 주기의 시스템별 신호 범위 — 같은 PLC 의 flow 들이 한 번만 잰다. 주기마다 비운다.</summary>
    private readonly Dictionary<Guid, (long? OldestMs, long? LatestMs)> _spanBySystem = new();

    public CycleIngestService(
        KpiRepository repo,
        BaselineService baselines,
        AppSettingsService settings,
        IServiceScopeFactory scopes,
        PlcToCallMapperService mapper,
        CycleSourceDeriver deriver,
        IFlowMetricsService flowMetrics,
        DsProjectService project,
        IPlcRepository plc,
        OeeCommHealthService commHealth,
        ILogger<CycleIngestService> logger)
    {
        _repo = repo;
        _baselines = baselines;
        _settings = settings;
        _scopes = scopes;
        _mapper = mapper;
        _deriver = deriver;
        _flowMetrics = flowMetrics;
        _project = project;
        _plc = plc;
        _commHealth = commHealth;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _writeGate.WaitAsync(stoppingToken);
                try
                {
                    await RunRebuildsAsync(stoppingToken);

                    var n = await IngestOnceAsync(stoppingToken);
                    if (n > 0)
                    {
                        _baselines.Invalidate();
                        _logger.LogInformation("[Kpi] ingested {Count} cycle(s)", n);
                    }

                    // 표본이 쌓여 기준선이 생겼으면, 기준선 없이 들어온 행에 뒤늦게 찍어 준다.
                    var b = await BackfillBaselinesAsync(stoppingToken);
                    if (b > 0) _logger.LogInformation("[Kpi] baseline stamped on {Count} pending cycle(s)", b);
                }
                finally { _writeGate.Release(); }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Kpi] cycle ingest failed");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>한 배치 적재. 반환값은 저장한 사이클 수.</summary>
    public async Task<int> IngestOnceAsync(CancellationToken ct)
    {
        // 추적 flow·경계 Call 이름은 라이브 엔진이 모델에서 해석해 둔 것 — 초기화 전이면 다음 주기에.
        if (!_flowMetrics.IsInitialized) return 0;

        _spanBySystem.Clear();
        var watermarks = await ReadWatermarksAsync(ct);
        var kpi = _settings.LoadSettings().Kpi;
        double gate = kpi.ResolveWorkGate();
        long snapMs = kpi.ResolveBoundarySnapMs();

        int saved = 0;
        foreach (var flow in _flowMetrics.GetTrackedFlowNames())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                saved += await IngestFlowAsync(flow, watermarks.TryGetValue(flow, out var m) ? m : null, gate, snapMs, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Kpi] cycle ingest failed — flow={Flow}", flow);
            }
        }
        return saved;
    }

    /// <summary>flow 하나 — 워터마크 이후 닫힌 사이클을 도출해 측정·박제한다. 반환값은 저장한 사이클 수.</summary>
    private async Task<int> IngestFlowAsync(string flow, long? watermark, double gate, long snapMs, CancellationToken ct)
    {
        var rows = await ReadPendingAsync(flow, watermark, ct);
        if (rows.Count == 0) return 0;

        // 기준선 = 지금 값(BaselineService 의 14일 이동 창). 사이클 완료 직후 적재되므로 '그 시점 값' 과 같다.
        var measured = await MeasureBatchAsync(flow, rows, gate, snapMs, async (branch, _) => (
                await _baselines.GetRAsync(flow, branch, ct),
                await _baselines.GetMtAsync(flow, branch, ct),
                await _baselines.GetWAsync(flow, branch, ct)),
            onMeasured: null, ct);
        if (measured is null)
        {
            _logger.LogInformation("[Kpi] 미계측 조회 실패 — flow={Flow} 적재를 다음 주기로 미룸", flow);
            return 0;
        }

        int saved = 0;
        foreach (var (record, works) in measured)
            if (await _repo.SaveCycleAsync(record, works, ct) > 0) saved++;
        return saved;
    }

    /// <summary>사이클 시작 시각 기준 기준선(R · MT중앙 · W). 없으면 null/빈 사전.</summary>
    private delegate Task<(double? R, double? Mt, IReadOnlyDictionary<string, WorkBaseline> W)> BaselineAt(string? branch, long atMs);

    /// <summary>
    /// 닫힌 사이클 묶음을 측정하고 기준선을 박제한 행으로 만든다 — 적재와 재적재가 공유한다.
    /// 미계측 조회가 실패하면 null(호출측이 미룬다 — 박제는 나중에 못 고친다).
    /// <paramref name="onMeasured"/> 는 행이 만들어질 때마다 순서대로 불린다(재적재가 다음 사이클의 기준선 창에 표본을 넣는다).
    /// </summary>
    private async Task<List<(CycleRecord Cycle, IReadOnlyList<WorkDuration> Works)>?> MeasureBatchAsync(
        string flow, List<SourceCycle> rows, double gate, long snapMs, BaselineAt baselineAt,
        Action<CycleRecord, IReadOnlyList<WorkDuration>>? onMeasured, CancellationToken ct)
    {
        // 통신 공백(미계측) — 이 flow 의 시스템 기준, 배치 구간당 1회.
        var systemScope = SystemKeyConvention.Scope(_project.TryGetSystemIdByFlowName(flow));
        var (gaps, trusted) = await _commHealth.TryGetUnmeasuredWindowsAsync(
            KpiTime.ToUtc(rows[0].StartMs), KpiTime.ToUtc(rows[^1].EndMs), ct, systemScope);
        if (!trusted) return null;

        var callSpans = await LoadCallSpansAsync(
            flow, rows[0].StartMs - SignalPadMs, rows[^1].EndMs + SignalPadMs, ct);

        // 스냅 대상 경계 = 이 배치의 모든 사이클 시작·끝(오름차순).
        var boundaries = rows.SelectMany(r => new[] { r.StartMs, r.EndMs }).Distinct().OrderBy(x => x).ToList();

        var branchSet = _settings.GetFlowBranchSet(flow);
        bool hasBranches = branchSet is { Branches.Count: > 0 };

        // 경계를 주소(태그 지정)로 고른 분기/flow 만 값이 있다 — 그 주소의 call 은 경계에서 구간을 연다(§2.2.1). 분기마다 경계가 다르므로 캐시한다.
        var headByBranch = new Dictionary<string, BoundaryHead?>(StringComparer.OrdinalIgnoreCase);

        var result = new List<(CycleRecord, IReadOnlyList<WorkDuration>)>(rows.Count);
        foreach (var src in rows)
        {
            // 분기가 있는 flow 에서 어느 분기도 아니면 미분류 — 계산·표본 밖. 구간은 재서 보여 주되 기준선은 박제하지 않는다.
            bool unclassified = hasBranches && string.IsNullOrWhiteSpace(src.Branch);
            // 통신 공백과 겹치면 다음 경계가 공백 뒤로 밀린 것일 수 있다 — 가짜 비생산·비가동을 만들지 않게 제외.
            // 3분 미만 공백은 보고되지 않아(OeeCommHealthService.MinReportGapMs) 사이클로 남는다(의도).
            bool commGap = gaps.Any(g => g.S < src.EndMs && g.E > src.StartMs);
            var excl = unclassified ? EmptyNames : ExcludedCallsOf(branchSet, src.Branch);

            var headKey = src.Branch ?? "";
            if (!headByBranch.TryGetValue(headKey, out var head))
                headByBranch[headKey] = head = ResolveBoundaryHead(flow, branchSet, src.Branch);

            var (measured, mt, overflow) = MeasureCycle(
                callSpans, excl, src.StartMs, src.EndMs, boundaries, snapMs, head);

            CycleRecord record;
            IReadOnlyList<WorkDuration> works;
            if (commGap || unclassified)
            {
                works = measured.Select(m => new WorkDuration(m.Work, m.DurationMs, 0)).ToList();
                record = new CycleRecord(flow, src.Branch, src.StartMs, src.EndMs, mt, 0, 0, null, 0, overflow,
                    commGap ? ExcludeReason.Unknown : ExcludeReason.Unclassified);
            }
            else
            {
                var (r, mtMed, wBase) = await baselineAt(src.Branch, src.StartMs);
                (var stamped, var worstWork, var worstRatio) = Stamp(measured, wBase, gate);
                works = stamped;
                // 너무 짧은 빈 사이클(doc/30 §3) — work 없이 W_min 보다 짧으면 가짜 경계(head 이중 상승). 기준 없음보다 앞선다:
                // 기준선이 없어도 길이로 이미 생산 사이클이 아니고, 표본에도 들어가면 안 된다.
                var exclude = KpiRules.IsTooShort(mt, src.EndMs - src.StartMs, wBase) ? ExcludeReason.TooShort
                    : r is null ? ExcludeReason.NoBaseline
                    : ExcludeReason.None;
                record = new CycleRecord(
                    flow,
                    src.Branch,
                    src.StartMs,
                    src.EndMs,
                    mt,
                    r ?? 0,
                    mtMed ?? 0,
                    worstWork,
                    worstRatio,
                    overflow,
                    exclude);
            }

            result.Add((record, works));
            onMeasured?.Invoke(record, works);
        }
        return result;
    }

    // ── 재적재 ───────────────────────────────────────────────────────────────

    /// <summary>재적재 진행 상태(관리자 화면 폴링).</summary>
    public KpiRebuildStatus RebuildStatus => _rebuildStatus with { Pending = _rebuildQueue.Count };

    /// <summary>
    /// 재적재 요청. <paramref name="flow"/> null = 추적 중인 전 flow. 다음 적재 주기(최대 30초 뒤)에 처리된다.
    /// 반환값은 대기열에 들어간 flow 수.
    /// </summary>
    public int RequestRebuild(string? flow)
    {
        var flows = flow is null
            ? (_flowMetrics.IsInitialized ? _flowMetrics.GetTrackedFlowNames() : Array.Empty<string>())
            : [flow];
        foreach (var f in flows) _rebuildQueue[f] = 0;
        return flows.Count;
    }

    /// <summary>대기 중인 재적재를 처리한다. 기동 후 처음 한 번은 규칙 버전이 다른 행이 있는 flow 를 대기열에 넣는다.</summary>
    private async Task RunRebuildsAsync(CancellationToken ct)
    {
        if (!_flowMetrics.IsInitialized) return;

        if (!_specChecked)
        {
            _specChecked = true;
            foreach (var flow in _flowMetrics.GetTrackedFlowNames())
            {
                var (oldest, _) = await _plc.GetSignalSpanMsAsync(_project.TryGetSystemIdByFlowName(flow));
                if (oldest is long from && await _repo.CountStaleSpecAsync(flow, from, ct) > 0)
                    _rebuildQueue[flow] = 0;
            }
            if (!_rebuildQueue.IsEmpty)
                _logger.LogInformation("[Kpi] 판정 규칙 {Spec} 이전 행 발견 — 재적재 대기 {Count} flow", KpiDb.SpecVersion, _rebuildQueue.Count);
        }

        if (_rebuildQueue.IsEmpty) return;

        var kpi = _settings.LoadSettings().Kpi;
        double gate = kpi.ResolveWorkGate();
        long snapMs = kpi.ResolveBoundarySnapMs();

        int done = 0, cycles = 0;
        string? lastError = null;
        foreach (var flow in _rebuildQueue.Keys.OrderBy(f => f, StringComparer.Ordinal).ToList())
        {
            ct.ThrowIfCancellationRequested();
            _rebuildStatus = _rebuildStatus with { Running = true, Flow = flow, Done = done, Cycles = cycles };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var (saved, retry, error) = await RebuildFlowAsync(flow, gate, snapMs, ct);
                if (retry) continue;   // 미계측 조회 실패 — 대기열에 남겨 다음 주기에 다시
                _rebuildQueue.TryRemove(flow, out _);
                done++;
                cycles += saved;
                if (error is not null) lastError = $"{flow}: {error}";
                _logger.LogInformation("[Kpi] 재적재 {Flow}: {Count} 사이클 ({Ms} ms){Err}",
                    flow, saved, sw.ElapsedMilliseconds, error is null ? "" : " — " + error);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _rebuildQueue.TryRemove(flow, out _);
                lastError = $"{flow}: {ex.Message}";
                _logger.LogWarning(ex, "[Kpi] 재적재 실패 — flow={Flow} (기존 행 유지)", flow);
            }
        }

        _baselines.Invalidate();
        _rebuildStatus = new KpiRebuildStatus(false, null, 0, done, cycles, lastError, DateTimeOffset.Now, KpiDb.SpecVersion);
    }

    /// <summary>
    /// flow 하나를 원시 신호가 남은 전 구간에 대해 다시 만든다. 다 만든 뒤 한 트랜잭션으로 교체한다 —
    /// 도중에 실패하면 기존 행이 그대로 남는다. Retry = 미계측 조회 실패(다음 주기에 다시). Error = 경계 미해석(사용자가 고칠 것).
    /// </summary>
    private async Task<(int Saved, bool Retry, string? Error)> RebuildFlowAsync(
        string flow, double gate, long snapMs, CancellationToken ct)
    {
        var (oldestMs, latestMs) = await _plc.GetSignalSpanMsAsync(_project.TryGetSystemIdByFlowName(flow));
        if (oldestMs is not long from || latestMs is not long latest) return (0, false, null);   // 신호 없음 — 할 일 없음(실패 아님)
        long upper = latest - SettleMs;
        if (upper <= from) return (0, false, null);

        var (head, tail) = ResolveHeadTail(flow);

        // 기준선 창 — 지우지 않는 직전 14일 행을 먼저 싣고, 다시 만든 행을 시간 순으로 더해 간다.
        var window = new AsOfBaselines();
        foreach (var row in await _repo.GetSampleRowsAsync(flow, from - BaselineWindowMs, from, ct))
            window.Add(row.Branch, row.StartMs, row.CtMs, row.MtMs, row.Works);

        var all = new List<(CycleRecord Cycle, IReadOnlyList<WorkDuration> Works)>();
        long cursor = from;
        while (cursor < upper)
        {
            ct.ThrowIfCancellationRequested();
            var rows = await ReadClosedAsync(flow, head, tail, cursor, upper);
            if (rows is null) return (0, false, "시작 경계 미해석 — 기존 행 유지");
            if (rows.Count == 0) break;

            var measured = await MeasureBatchAsync(flow, rows, gate, snapMs,
                (branch, at) => Task.FromResult(window.At(branch, at)),
                (rec, works) =>
                {
                    if (rec.Exclude is ExcludeReason.None or ExcludeReason.NoBaseline)
                        window.Add(rec.Branch, rec.StartMs, rec.CtMs, rec.MtMs, works.Select(w => (w.Work, w.DurationMs)).ToList());
                }, ct);
            if (measured is null) return (0, true, null);

            all.AddRange(measured);
            cursor = rows[^1].EndMs;
        }

        if (all.Count == 0) return (0, false, null);   // 닫힌 사이클 없음 — 기존 행 유지(실패 아님)
        await _repo.ReplaceCyclesAsync(flow, from, all, ct);
        return (all.Count, false, null);
    }

    private const long BaselineWindowMs = KpiRules.BaselineWindowDays * 86_400_000L;

    /// <summary>
    /// 재적재용 '그 시점' 기준선 — [t − 14일, t) 에 시작한 표본의 중앙값·사분위(BaselineService 와 같은 정의).
    /// 시각이 단조 증가하므로 창 앞쪽은 포인터로 버린다. 계산은 10분 구간마다 한 번(BaselineService 의 1분 캐시와 같은 취지 —
    /// 수만 행을 사이클마다 정렬하지 않게).
    /// </summary>
    private sealed class AsOfBaselines
    {
        private const long BucketMs = 600_000;
        private readonly Dictionary<string, Scope> _scopes = new(StringComparer.Ordinal);

        private sealed class Scope
        {
            public readonly List<(long T, long V)> Ct = [];
            public readonly List<(long T, long V)> Mt = [];
            public readonly List<(long T, string W, long V)> Works = [];
            public int CtHead, MtHead, WHead;
            public long Bucket = long.MinValue;
            public (double? R, double? Mt, IReadOnlyDictionary<string, WorkBaseline> W) Cached;
        }

        private Scope Of(string? branch)
        {
            var key = branch ?? "";
            if (!_scopes.TryGetValue(key, out var sc)) _scopes[key] = sc = new Scope();
            return sc;
        }

        public void Add(string? branch, long startMs, long ctMs, long? mtMs, IReadOnlyList<(string Work, long DurationMs)> works)
        {
            var sc = Of(branch);
            if (ctMs > 0) sc.Ct.Add((startMs, ctMs));
            if (mtMs is long mt && mt > 0) sc.Mt.Add((startMs, mt));
            foreach (var (w, d) in works) if (d > 0) sc.Works.Add((startMs, w, d));
        }

        public (double? R, double? Mt, IReadOnlyDictionary<string, WorkBaseline> W) At(string? branch, long t)
        {
            var sc = Of(branch);
            long bucket = t / BucketMs;
            if (bucket == sc.Bucket) return sc.Cached;

            long since = t - BaselineWindowMs;
            while (sc.CtHead < sc.Ct.Count && sc.Ct[sc.CtHead].T < since) sc.CtHead++;
            while (sc.MtHead < sc.Mt.Count && sc.Mt[sc.MtHead].T < since) sc.MtHead++;
            while (sc.WHead < sc.Works.Count && sc.Works[sc.WHead].T < since) sc.WHead++;

            var r = KpiRules.Median(Slice(sc.Ct, sc.CtHead, t));
            var mt = KpiRules.Median(Slice(sc.Mt, sc.MtHead, t));
            var byWork = new Dictionary<string, List<long>>(StringComparer.Ordinal);
            for (int i = sc.WHead; i < sc.Works.Count && sc.Works[i].T < t; i++)
            {
                var (_, w, v) = sc.Works[i];
                if (!byWork.TryGetValue(w, out var list)) byWork[w] = list = [];
                list.Add(v);
            }
            var wMap = new Dictionary<string, WorkBaseline>(StringComparer.Ordinal);
            foreach (var (w, list) in byWork)
                if (KpiRules.QuartilesOf(list) is Quartiles q) wMap[w] = new WorkBaseline(q.Median, q.Q1, q.Q3, q.Count);

            sc.Bucket = bucket;
            sc.Cached = (r, mt, wMap);
            return sc.Cached;
        }

        private static List<long> Slice(List<(long T, long V)> src, int head, long t)
        {
            var list = new List<long>(Math.Max(0, src.Count - head));
            for (int i = head; i < src.Count && src[i].T < t; i++) list.Add(src[i].V);
            return list;
        }
    }

    /// <summary>
    /// 기준선 없이 적재된 행에 기준선을 뒤늦게 박제한다. 반환값은 처리한 행 수.
    /// <para>
    /// 설치 직후에는 표본이 없어 첫 사이클들이 전부 '기준 없음' 으로 들어온다. 표본 10건이 쌓이면
    /// 그 행들도 판정 대상이 되어야 한다 — 안 그러면 첫 묶음만 영구히 계산 밖에 남아, 수집 첫날
    /// 지표가 통째로 비게 된다(실측: 첫 적재 400건이 모두 제외).
    /// </para>
    /// </summary>
    public async Task<int> BackfillBaselinesAsync(CancellationToken ct)
    {
        var pending = await _repo.GetPendingBaselineCyclesAsync(BackfillLimit, ct);
        if (pending.Count == 0) return 0;

        double gate = _settings.LoadSettings().Kpi.ResolveWorkGate();

        int done = 0;
        foreach (var (id, flow, branch, cycleCtMs, cycleMtMs) in pending)
        {
            ct.ThrowIfCancellationRequested();
            if (await _baselines.GetRAsync(flow, branch, ct) is not double r) continue;
            var mtMed = await _baselines.GetMtAsync(flow, branch, ct) ?? 0;
            var wBase = await _baselines.GetWAsync(flow, branch, ct);

            // 부팅 구간에 기준 없음으로 들어온 가짜 경계(work 없는 짧은 행)는 R 을 찍어 가동으로 살리면 안 된다 — 너무 짧음으로 확정.
            if (KpiRules.IsTooShort(cycleMtMs, cycleCtMs, wBase))
            {
                if (await _repo.MarkTooShortAsync(id, ct)) done++;
                continue;
            }

            var works = await _repo.GetCycleWorksAsync(id, ct);
            var (stamped, worstWork, worstRatio) = Stamp(works.Select(w => (w.Work, w.DurationMs)).ToList(), wBase, gate);

            if (await _repo.StampBaselineAsync(id, r, mtMed, worstWork, worstRatio, stamped, ct)) done++;
        }
        return done;
    }

    // ── 측정 ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 한 call 의 이름·소속 work·구간 목록(§2.2 규칙으로 만든 것). <paramref name="Markers"/> = 완료 마커(o~i 면 IN↑, o~o 면 OUT↓,
    /// <see cref="WorkSpanMath.CompletionMarkers"/>) — 이 call 이 경계 고정 head(§2.2.1)일 때 구간의 끝으로 쓰인다.
    /// </summary>
    public sealed record CallSpanSet(string CallName, string Work, List<Span> Spans, IReadOnlyList<long>? Markers = null);

    /// <summary>
    /// 사이클 경계를 <b>주소(태그 지정)</b>로 고른 flow/분기의 head call(doc/30 §2.2.1) — <see cref="ResolveBoundaryHead"/>.
    /// <paramref name="Anchored"/>=true 면 call 에 OUT 이 있어 사이클마다 [cs, 첫 완료 마커) 구간 하나를 만들고 OUT↑ 구간은 버린다.
    /// false 면 IN 전용 센서라 work 봉투를 cs 에서 열어 두기만 한다(끝은 형제 call).
    /// </summary>
    public sealed record BoundaryHead(string CallName, string Work, bool Anchored);

    /// <summary>
    /// 사이클 경계를 <b>주소(태그 지정)</b>로 고른 flow/분기의 head call(doc/30 §2.2.1). 사용자가 지목한 신호가 사이클 시작이므로
    /// 그 주소의 call 은 §2.2 의 OUT↑ 가 아니라 <b>경계(cs)에서 구간을 연다</b>.
    /// <list type="bullet">
    ///   <item>주소가 그 call 의 OUT 이고 에지가 상승이면 null — §2.2 와 결과가 같아 손댈 것이 없다(Call 지정 기본값과 동일).</item>
    ///   <item>call 에 OUT 이 있으면 Anchored=true — 그 call 의 OUT↑ 구간은 버리고 사이클마다 [cs, 첫 완료 마커) 하나만 만든다.
    ///     종전엔 경계보다 앞서 뜬 OUT↑ 구간이 <b>앞 사이클</b>에 귀속돼 앞 사이클 MT 를 경계까지 늘리고 WT 를 0 으로 만들었다.</item>
    ///   <item>call 에 OUT 이 없으면(IN 전용 센서) Anchored=false — 그 work 봉투를 cs 에서 열어 두기만 한다(시작점 시드).
    ///     끝은 형제 call 이 정하고, 형제가 없으면 폭 0 이라 제외된다. 관측 전용 work 는 지속시간이 원리상 없다.</item>
    /// </list>
    /// 모델이 아직 안 섰거나 주소가 이 flow 에 없으면 null — 판단 근거가 없으면 종전 동작을 유지한다.
    /// </summary>
    private BoundaryHead? ResolveBoundaryHead(string flow, FlowBranchSet? branchSet, string? branch)
    {
        try
        {
            string? address = null, edge = null;
            if (!string.IsNullOrWhiteSpace(branch))
            {
                // 분기 행은 그 분기의 경계가 정본. 정의를 못 찾으면(이름 변경 등) 손대지 않는다.
                var def = branchSet?.Branches
                    .FirstOrDefault(b => string.Equals(b.Name, branch, StringComparison.OrdinalIgnoreCase));
                address = def?.StartTagAddress;
                edge = def?.StartTagEdge;
            }
            else if (branchSet is not { Branches.Count: > 0 })
            {
                var ov = _settings.GetFlowCycleOverride(flow);
                address = ov?.StartTagAddress;
                edge = ov?.StartTagEdge;
            }

            if (!CycleBoundaryEdges.HasTagSpec(address)) return null;
            var addr = address!.Trim();

            if (!_mapper.IsInitialized) _mapper.Initialize();
            var hit = _mapper.GetFlowTagCatalog(flow)
                .FirstOrDefault(t => string.Equals(t.Address, addr, StringComparison.OrdinalIgnoreCase));
            if (hit is null) return null;

            // OUT 상승 = §2.2 가 이미 경계에서 구간을 연다 — 손댈 것이 없다.
            if (!hit.IsIn && !CycleBoundaryEdges.IsFallingEdge(edge)) return null;

            var pairs = _mapper.GetCallTagPairsByCallId(hit.CallId);
            bool hasOut = pairs.Any(p => !string.IsNullOrWhiteSpace(p.OutTag));
            var work = string.IsNullOrWhiteSpace(hit.WorkName) ? hit.CallName : hit.WorkName;
            return new BoundaryHead(hit.CallName, work, Anchored: hasOut);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Kpi] 경계 head call 해석 실패 — flow={Flow} branch={Branch}", flow, branch);
            return null;
        }
    }

    private static readonly HashSet<string> EmptyNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>이 분기의 제외 call 이름 집합. 분기 정의가 없거나 이름이 안 맞으면 빈 집합(= 아무것도 빼지 않음).</summary>
    private static HashSet<string> ExcludedCallsOf(FlowBranchSet? set, string? branch)
    {
        if (set is null || string.IsNullOrWhiteSpace(branch)) return EmptyNames;
        var def = set.Branches.FirstOrDefault(b => string.Equals(b.Name, branch, StringComparison.OrdinalIgnoreCase));
        if (def is null) return EmptyNames;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in def.ExcludedCallNames)
        {
            // 자기 시작/끝 call 이 제외 목록에 섞여 있으면 무시 — 분기 판별 경로와 같은 방어.
            if (string.Equals(n, def.StartCallName, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(n, def.EndCallName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(n)) names.Add(n.Trim());
        }
        return names;
    }

    /// <summary>
    /// 사이클 [cs, ce) 안의 work 구간·MT·경계 초과(doc/30 §2.3 · §2.4 · §3).
    /// <list type="bullet">
    ///   <item>제외 call 은 뺀다(call 단위). 남은 call 구간 중 시작이 이 사이클에 속하는 것만 본다 — 시작은 스냅 뒤 시각.</item>
    ///   <item>work 구간 = 그 work 에 속한 call 구간들의 최소 시작 ~ 최대 끝. 더하지 않는다.</item>
    ///   <item>MT = 경계 → 마지막 work 끝(사이클 끝에서 자름). work 사이 공백을 포함한다.</item>
    ///   <item>초과 = call 구간 끝이 사이클 끝을 넘은 최대량. 허용치 비교는 조회 시(κ).</item>
    /// </list>
    /// <para>
    /// <paramref name="head"/> = 경계를 <b>주소</b>로 고른 경우 그 주소의 call(§2.2.1, <see cref="ResolveBoundaryHead"/>).
    /// 사용자가 지목한 신호가 사이클 시작이므로 그 call 의 work 는 <b>cs 에서 열린다</b>.
    /// Anchored 면 그 call 의 OUT↑ 구간은 쓰지 않고 [cs, cs 이후 첫 완료 마커) 하나를 만든다 — 마커가 다음 경계 전에 없으면
    /// 폭 0(경계 표식으로만 쓰이고 MT 끝에 기여하지 않는다). Anchored 가 아니면(IN 전용) 봉투만 cs 에서 열고 끝은 형제 call 몫이다.
    /// 어느 쪽이든 그 work 에 다른 구간이 없으면 폭 0 으로 남아 아래 <c>e &gt; s</c> 에서 제외된다.
    /// </para>
    /// </summary>
    public static (List<(string Work, long DurationMs)> Works, long? MtMs, long OverflowMs) MeasureCycle(
        IReadOnlyList<CallSpanSet> calls, HashSet<string> excluded, long cs, long ce,
        IReadOnlyList<long> boundaries, long snapMs, BoundaryHead? head = null)
    {
        var env = new Dictionary<string, (long S, long E)>(StringComparer.Ordinal);
        long lastEnd = long.MinValue;
        long overflow = 0;

        if (head is not null) env[head.Work] = (cs, cs);

        foreach (var call in calls)
        {
            if (excluded.Count > 0 && excluded.Contains(call.CallName)) continue;

            if (head is { Anchored: true } && string.Equals(call.CallName, head.CallName, StringComparison.OrdinalIgnoreCase))
            {
                // 경계 고정 head — OUT↑ 구간 대신 [cs, 첫 마커). 마커는 cs 보다 뒤여야 한다(경계가 그 call 의 IN↑ 이면 그 IN↑ 자신은 제외).
                if (FirstMarkerIn(call.Markers, cs, ce) is long end)
                {
                    var cur = env[head.Work];
                    env[head.Work] = (Math.Min(cur.S, cs), Math.Max(cur.E, end));
                    if (end > lastEnd) lastEnd = end;
                }
                continue;
            }

            foreach (var span in call.Spans)
            {
                long start = WorkSpanMath.Snap(span.S, boundaries, snapMs);
                if (start < cs || start >= ce) continue;

                if (env.TryGetValue(call.Work, out var cur))
                    env[call.Work] = (Math.Min(cur.S, span.S), Math.Max(cur.E, span.E));
                else
                    env[call.Work] = (span.S, span.E);

                if (span.E > lastEnd) lastEnd = span.E;
                if (span.E > ce) overflow = Math.Max(overflow, span.E - ce);
            }
        }

        var works = new List<(string, long)>(env.Count);
        foreach (var (work, (s, e)) in env)
            if (e > s) works.Add((work, e - s));

        long? mt = lastEnd > cs ? Math.Min(lastEnd, ce) - cs : null;
        return (works, mt, overflow);
    }

    /// <summary>오름차순 <paramref name="markers"/> 중 cs 초과 · ce 미만인 첫 값. 없으면 null.</summary>
    private static long? FirstMarkerIn(IReadOnlyList<long>? markers, long cs, long ce)
    {
        if (markers is null || markers.Count == 0) return null;
        int lo = 0, hi = markers.Count;
        while (lo < hi) { int mid = (lo + hi) >> 1; if (markers[mid] <= cs) lo = mid + 1; else hi = mid; }
        return lo < markers.Count && markers[lo] < ce ? markers[lo] : null;
    }

    /// <summary>
    /// work 별 지속시간에 기준선 W 와 게이트를 박제하고, 게이트를 통과한 work 중 최악 배율을 고른다(doc/30 §4.1).
    /// 기준선이 없는 work 는 W=0 으로 남아 비교에서 빠진다.
    /// </summary>
    private static (List<WorkDuration> Works, string? WorstWork, double WorstRatio) Stamp(
        IReadOnlyList<(string Work, long DurationMs)> measured,
        IReadOnlyDictionary<string, WorkBaseline> wBase, double gate)
    {
        var list = new List<WorkDuration>(measured.Count);
        double worst = 0;
        string? worstWork = null;
        foreach (var (work, dur) in measured)
        {
            WorkDuration wd;
            if (wBase.TryGetValue(work, out var wb))
            {
                bool gated = KpiRules.IsGated(new Quartiles(wb.Q1Ms, wb.MedianMs, wb.Q3Ms, wb.SampleCount), gate);
                wd = new WorkDuration(work, dur, wb.MedianMs, gated);
            }
            else wd = new WorkDuration(work, dur, 0);

            list.Add(wd);
            if (!wd.Gated && wd.Ratio > worst) { worst = wd.Ratio; worstWork = work; }
        }
        return (list, worstWork, worst);
    }

    // ── 출처: 원시 신호에서 도출한 닫힌 사이클 ─────────────────────────────────────

    private sealed record SourceCycle(string Flow, string? Branch, long StartMs, long EndMs);

    /// <summary>
    /// 워터마크(이 flow 의 마지막 적재 사이클 끝) 이후, 시스템 최신 신호 − 정착 여유 안에서 <b>닫힌</b> 사이클.
    /// 마지막 열린 사이클(다음 경계 없음)은 다음 주기로 넘긴다. 최대 <see cref="BatchLimit"/> 건.
    /// <para>워터마크가 없으면(첫 적재) 기준선 창(14일)만큼만 거슬러 올라간다 — 전 이력 재적재는 별도 작업.</para>
    /// </summary>
    private async Task<List<SourceCycle>> ReadPendingAsync(string flow, long? watermark, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var systemId = _project.TryGetSystemIdByFlowName(flow);
        var key = systemId ?? Guid.Empty;
        if (!_spanBySystem.TryGetValue(key, out var span))
            _spanBySystem[key] = span = await _plc.GetSignalSpanMsAsync(systemId);
        var (oldestMs, latestMs) = span;
        if (oldestMs is not long oldest || latestMs is not long latest) return [];

        long upper = latest - SettleMs;
        long from = watermark ?? Math.Max(oldest, latest - KpiRules.BaselineWindowDays * 86_400_000L);
        if (upper <= from) return [];

        var (head, tail) = ResolveHeadTail(flow);
        var list = await ReadClosedAsync(flow, head, tail, from, upper);
        if (list is null) return [];

        if (list.Count > BatchLimit) list.RemoveRange(BatchLimit, list.Count - BatchLimit);
        return list;
    }

    /// <summary>Head/Tail = 간트(CallTestController.ResolveEffectiveHeadTail)와 같은 순서 — 런타임 경계(override 적용) &gt; AASX.</summary>
    private (string? Head, string? Tail) ResolveHeadTail(string flow)
    {
        var (head, tail) = _flowMetrics.GetCycleBoundaryCallNames(flow);
        if (string.IsNullOrEmpty(head) && string.IsNullOrEmpty(tail))
            (head, tail) = _flowMetrics.GetAasxCycleBoundaries(flow);
        return (head, tail);
    }

    /// <summary>
    /// from 부터 닫힌 사이클 — 최대 <see cref="MaxSpanMs"/> 구간을 보고, 그 안에 하나도 없으면(장기 정지) 상한까지 한 번 넓힌다.
    /// 시작 경계 미해석이면 null.
    /// </summary>
    private async Task<List<SourceCycle>?> ReadClosedAsync(string flow, string? head, string? tail, long from, long upper)
    {
        long to = Math.Min(upper, from + MaxSpanMs);
        var list = await DeriveClosedAsync(flow, head, tail, from, to);
        if (list is { Count: 0 } && to < upper)
            list = await DeriveClosedAsync(flow, head, tail, from, upper);
        return list;
    }

    /// <summary>[from, to) 의 닫힌 사이클(시작 ≥ from). 시작 경계 미해석이면 null(경고는 flow 당 한 번).</summary>
    private async Task<List<SourceCycle>?> DeriveClosedAsync(string flow, string? head, string? tail, long from, long to)
    {
        // 엣지 판정은 구간 첫 행의 직전 값을 알아야 한다 — 앞 여유를 두고 받은 뒤 from 이전 시작은 버린다.
        var fromLocal = KpiTime.ToLocal(from - SignalPadMs);
        var toLocal = KpiTime.ToLocal(to);
        var derived = await _deriver.DeriveAsync(flow, head, tail, fromLocal, toLocal);

        // Head 에 OUT 이 없어 시작을 못 정하면 간트처럼 AASX Head 로 한 번 더(CallTestController.ResolveBoundariesAsync 폴백).
        if (derived.Status == DeriveStatus.Unresolved)
        {
            var (aasxHead, aasxTail) = _flowMetrics.GetAasxCycleBoundaries(flow);
            if (!string.IsNullOrEmpty(aasxHead) && !string.Equals(aasxHead, head, StringComparison.OrdinalIgnoreCase))
                derived = await _deriver.DeriveAsync(flow, aasxHead, aasxTail, fromLocal, toLocal);
        }

        if (derived.Status == DeriveStatus.Unresolved)
        {
            if (_warnedUnresolved.Add(flow))
                _logger.LogWarning("[Kpi] '{Flow}' 시작 경계 미해석 ({What}) — 판정 적재 건너뜀", flow, derived.UnresolvedLabel);
            return null;
        }
        _warnedUnresolved.Remove(flow);

        var list = new List<SourceCycle>();
        foreach (var c in derived.Cycles)
        {
            if (c.PeriodMs is not double period) continue;   // 열린 사이클 — 다음 경계가 와야 닫힌다
            long start = KpiTime.ToMs(c.Start);
            if (start < from) continue;
            list.Add(new SourceCycle(flow, c.Branch, start, start + (long)Math.Round(period)));
        }
        return list;
    }

    private async Task<Dictionary<string, long>> ReadWatermarksAsync(CancellationToken ct)
    {
        try { return await _repo.GetCycleWatermarksAsync(ct); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Kpi] watermark read failed — 전체 재적재로 진행(저장이 멱등이라 무해)");
            return new Dictionary<string, long>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// flow 의 call 별 구간(doc/30 §2.2). 신호에서 call 마다 OUT ON 구간과 IN 상승을 모아 유형(o~i / o~o)을 정하고
    /// 구간을 만든다. IN 만 있는 call 은 여기서 빠진다. 채터 필터 없는 원본을 쓴다.
    /// </summary>
    private async Task<List<CallSpanSet>> LoadCallSpansAsync(
        string flow, long fromMs, long toMs, CancellationToken ct)
    {
        var result = new List<CallSpanSet>();
        try
        {
            using var scope = _scopes.CreateScope();
            var analysis = scope.ServiceProvider.GetRequiredService<CycleAnalysisService>();

            var data = await analysis.GetActualIoSignalSegmentsInTimeRangeAsync(
                flow, KpiTime.ToLocal(fromMs), KpiTime.ToLocal(toMs), maxItems: null);

            // call → (이름, work, OUT ON 구간, IN 상승)
            var byCall = new Dictionary<Guid, (string Name, string Work, List<(long Rise, long Fall)> Outs, List<long> Ins)>();
            foreach (var item in data.Items)
            {
                var work = string.IsNullOrWhiteSpace(item.WorkName) ? item.CallName : item.WorkName;
                if (string.IsNullOrWhiteSpace(work)) continue;

                if (!byCall.TryGetValue(item.CallId, out var c))
                    byCall[item.CallId] = c = (item.CallName ?? "", work, [], []);

                long rise = KpiTime.ToMs(item.GoingStartTime);
                if (item.EventType == IOEventType.OutTag)
                {
                    // 하강을 모르는 열린 구간은 Fall ≤ Rise 로 넘겨 o~o 에서 버려지게 한다.
                    long fall = item.FinishTime is DateTime ft ? KpiTime.ToMs(ft) : rise;
                    c.Outs.Add((rise, fall));
                }
                else c.Ins.Add(rise);
            }

            foreach (var (_, c) in byCall)
            {
                var spans = WorkSpanMath.CallSpans(c.Outs, c.Ins);
                var markers = WorkSpanMath.CompletionMarkers(c.Outs, c.Ins);
                // 마커만 있는 call(구간은 못 만들었지만 OUT 은 있는)도 넘긴다 — 경계 고정 head 면 마커가 구간의 끝이다(§2.2.1).
                if (spans.Count > 0 || markers.Count > 0) result.Add(new CallSpanSet(c.Name, c.Work, spans, markers));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Kpi] call span load failed — flow={Flow}", flow);
        }
        return result;
    }
}

/// <summary>판정 재적재 상태 — 관리자 화면 폴링용(camelCase 직렬화).</summary>
public sealed record KpiRebuildStatus(
    bool Running,
    string? Flow,
    int Pending,
    int Done,
    int Cycles,
    string? LastError,
    DateTimeOffset? FinishedAt,
    string Spec)
{
    public static KpiRebuildStatus Idle => new(false, null, 0, 0, 0, null, null, KpiDb.SpecVersion);
}
