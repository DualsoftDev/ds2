using System;
using System.Threading;
using System.Windows.Threading;
using Ds2.Core;

namespace Promaker.ViewModels;

/// <summary>
/// self-hosted 통신 blackout — PLC 단절 또는 신호 무소식(예: 상대 인스턴스 정지) 구간 처리.
/// backend(Agent) HubSession 의 blackout 상태머신(c3a38ff1)과 동형 철학:
///   두절 구간은 신호 순서/edge 를 신뢰할 수 없다 → actual 은 멈추고(증거 없는 진행 금지),
///   abnormal 발행은 억제, 진행 중 관측·학습 측정은 무효화. plan(추정)은 계속 — coast.
/// 해제는 "연결됨" status 가 아니라 신호 재개(resync 포함)만 신뢰한다.
/// (1차 라이트 — backend 의 per-call REARMING 은 무효화가 1차 방어라 생략. 재발 시 보강.)
/// </summary>
public partial class SimulationPanelState
{
    /// <summary>신호 무소식 → blackout 진입 임계의 바닥(ms). 실 임계는 적응형 —
    /// <see cref="ResolveAdaptiveSilenceThresholdMs"/> (관측 최대 신호 간격 × 마진, 바닥 이 값).
    /// 고정 3s 만으로는 실 PLC(신호 edge 간격이 수 초인 설비)에서 "진입→신호→해제→진입"
    /// 루프가 돈다(실기 확인) — 설비의 실제 신호 리듬을 보고 임계가 자란다.</summary>
    internal const int CommSilenceTimeoutMs = 3000;

    /// <summary>적응 임계 마진 — 관측된 최대 신호 간격의 몇 배까지를 "정상 침묵"으로 볼지.
    /// 가짜 blackout(관측·학습 무효화 부작용)이 늦은 감지보다 해롭다 — 보수적으로 3배.</summary>
    internal const double SilenceGapMarginFactor = 3.0;

    /// <summary>blackout 해제 간격의 학습 허용 배수 — 해제 시 간격이 (당시 임계 × 이 값) 이내면
    /// 진짜 두절이 아니라 "그 설비의 자연 간격"으로 보고 임계 학습에 반영한다.
    /// 이게 없으면 자연 간격이 항상 임계보다 큰 설비는 간격이 매번 blackout 중에 끝나
    /// 영영 학습이 안 되고 진입/해제 루프가 멈추지 않는다. 장기 두절(배수 초과)은 오염이라 제외.</summary>
    internal const double SilenceGapLearnableFactor = 3.0;

    /// <summary>열린 간트 바의 "증거 cap" 유예(ms) — 마지막 신호 후 이 시간까지는 바가 현재 시각을
    /// 따라가고, 넘으면 그 자리(마지막 신호+유예)에서 성장만 멈춘다(되감지 않음 — 아래 cap 주석).
    /// 신호는 폴링이 아니라 변화(edge)에만 오므로 정상 간격이 디바이스 동작 ~500ms·사이클 갭 ~1s —
    /// 그보다 충분히 커야 평시 발동이 없다(300ms 였을 때 전 모드 바가 신호 간격마다 깜빡인 실기 회귀).
    /// blackout 확정(3s) 전에 성장이 멎어 동결 시 되감김이 3s → ~1.5s 로 준다.</summary>
    internal const int OpenSegmentEvidenceGraceMs = 1500;

    private volatile bool _commBlackout;
    internal bool IsCommBlackout => _commBlackout;
    private long _lastSignalWallTicks;   // hub 스레드가 Interlocked 로 갱신
    private double _maxObservedSignalGapMs;   // 정상 운전 중 관측된 최대 신호 간격 — 적응 임계의 근거
    private DispatcherTimer? _commWatchdogTimer;

    /// <summary>적응 무소식 임계(ms) = max(바닥 3s, 관측 최대 간격 × 마진).</summary>
    internal static double ResolveAdaptiveSilenceThresholdMs(double maxObservedGapMs) =>
        Math.Max(CommSilenceTimeoutMs, maxObservedGapMs * SilenceGapMarginFactor);

    /// <summary>이 신호 간격을 임계 학습에 반영해도 되는가 — 정상 운전 간격은 항상,
    /// blackout 해제 간격은 "오탐 의심"(당시 임계 × 허용 배수 이내)일 때만(장기 두절 오염 차단).</summary>
    internal static bool ShouldLearnSignalGap(bool inBlackout, double gapMs, double thresholdMs) =>
        !inBlackout || gapMs <= thresholdMs * SilenceGapLearnableFactor;

    /// <summary>ctor 말미에서 1회 — Hub 신호/PLC 상태 구독 + 무소식 워치독 시작.</summary>
    private void InitCommBlackoutWatch()
    {
        Hub.TagBroadcast += (_, _, _) => OnCommSignalObserved();
        // 스캔 생존 heartbeat(~1s) — 태그 변화가 없어도 통신 생존 신호로 취급. 실 PLC 는
        // 무변화 침묵이 수 초씩 정상이라, 변화 이벤트만으로는 두절 감지가 사이클마다 오탐한다.
        Hub.ScanHeartbeat += OnCommSignalObserved;
        Hub.PlcConnectionStatusChanged += OnCommPlcStatus;
        // 열린 간트 바는 증거(마지막 신호) 시각까지만 — blackout 확정 전 3초 동안 바가
        // 빨간 선에 붙어 자라다 동결 시 되감기는 왜곡 방지. 렌더가 매 프레임 호출.
        GanttChart.OpenSegmentEvidenceCap = ResolveOpenSegmentEvidenceCap;
        _commWatchdogTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _commWatchdogTimer.Tick += (_, _) => OnCommWatchdogTick();
        _commWatchdogTimer.Start();
    }

    /// <summary>재개 후 첫 실측 Going 으로 shadow 추정 위치와 대조 대기 중.</summary>
    private bool _shadowReconcilePending;

    /// <summary>PLAY/RESET 시 — blackout 해제 + 신호 시각/적응 임계 초기화(첫 신호 전 무소식 오탐 방지).</summary>
    private void ResetCommBlackout()
    {
        _commBlackout = false;
        _shadowReconcilePending = false;
        _maxObservedSignalGapMs = 0;
        Interlocked.Exchange(ref _lastSignalWallTicks, 0);
    }

    /// <summary>렌더 프레임마다 호출 — 신호 기반 모드에서 마지막 신호 + 유예를 넘으면
    /// "마지막 신호 + 유예" 시각(간트 시계 좌표)을 열린 바의 끝 상한으로 반환. 그 외 null = 제한 없음.
    /// cap 은 마지막 신호 시각이 아니라 +유예 위치다 — 신호 시각으로 떨어뜨리면(스냅백) 신호
    /// 간격이 유예를 넘을 때마다 바가 줄었다 늘었다 깜빡인다(실기 회귀). 바는 앞으로만 간다.</summary>
    private DateTime? ResolveOpenSegmentEvidenceCap()
    {
        if (!IsSimulating || SelectedRuntimeMode == RuntimeMode.Simulation) return null;
        var last = Interlocked.Read(ref _lastSignalWallTicks);
        if (last == 0) return null;
        var sinceLast = TimeSpan.FromTicks(Math.Max(0, DateTime.Now.Ticks - last));
        // 유예도 적응 — 설비의 자연 신호 간격(관측 최대 × 1.2)보다 짧으면 바가 매 간격마다
        // 멈칫거린다(실 PLC 는 edge 간격이 수 초). 바닥은 고정 유예.
        var graceMs = Math.Max(OpenSegmentEvidenceGraceMs, _maxObservedSignalGapMs * 1.2);
        if (sinceLast.TotalMilliseconds <= graceMs) return null;
        // wall 경과를 간트 시계(AdjustedNow) 좌표로 환산 — EnterCommBlackout 의 freezeAt 과 동일 방식.
        return GanttChart.AdjustedNow - sinceLast + TimeSpan.FromMilliseconds(graceMs);
    }

    private void OnCommSignalObserved()
    {
        var nowTicks = DateTime.Now.Ticks;
        var prevTicks = Interlocked.Exchange(ref _lastSignalWallTicks, nowTicks);

        // 신호 간격 학습 — 설비의 실제 신호 리듬(관측 최대 간격)이 적응 임계의 근거.
        // blackout 해제 간격도 "오탐 의심" 범위면 학습 — 자연 간격이 임계보다 큰 설비가
        // 영영 학습 못 하고 진입/해제 루프를 도는 것(실 PLC 실기)을 한두 번 안에 수렴시킨다.
        if (prevTicks > 0)
        {
            var gapMs = (nowTicks - prevTicks) / (double)TimeSpan.TicksPerMillisecond;
            var threshold = ResolveAdaptiveSilenceThresholdMs(_maxObservedSignalGapMs);
            if (ShouldLearnSignalGap(_commBlackout, gapMs, threshold) && gapMs > _maxObservedSignalGapMs)
            {
                _maxObservedSignalGapMs = gapMs;
                if (gapMs * SilenceGapMarginFactor > CommSilenceTimeoutMs)
                    SimLog.Info($"[CommBlackout] 신호 간격 학습 — 관측 최대 {gapMs / 1000.0:F1}s, 무소식 임계 {ResolveAdaptiveSilenceThresholdMs(gapMs) / 1000.0:F1}s");
            }
        }

        if (_commBlackout)
            _dispatcher.BeginInvoke(new Action(() => ExitCommBlackout("신호 재개")));
    }

    private void OnCommPlcStatus(Ds2.Backend.Common.PlcConnectionStatus status)
    {
        // down 전이만 — up 은 신호 재개(OnCommSignalObserved)로만 해제 (backend 와 동일 철학:
        // connect 직후 read 가 전부 실패할 수 있어 "연결됨" 신호는 신뢰하지 않는다).
        if (status.IsConnected || _commBlackout) return;
        _dispatcher.BeginInvoke(new Action(() =>
            EnterCommBlackout($"PLC down ({status.Name}: {status.LastError})")));
    }

    private void OnCommWatchdogTick()
    {
        if (_commBlackout || !IsSimulating || SelectedRuntimeMode == RuntimeMode.Simulation)
            return;
        var last = Interlocked.Read(ref _lastSignalWallTicks);
        if (last == 0) return;   // 아직 신호를 한 번도 못 받음 — 시작 대기는 두절이 아님
        var silenceMs = (DateTime.Now.Ticks - last) / TimeSpan.TicksPerMillisecond;
        var thresholdMs = ResolveAdaptiveSilenceThresholdMs(_maxObservedSignalGapMs);
        if (silenceMs > thresholdMs)
            EnterCommBlackout($"신호 무소식 {silenceMs / 1000.0:F1}s (임계 {thresholdMs / 1000.0:F1}s)");
    }

    /// <summary>UI 스레드. actual 동결(마지막 신호 시각 기준) + 관측/학습 무효화 + abnormal 억제 시작.</summary>
    private void EnterCommBlackout(string reason)
    {
        if (_commBlackout || !IsSimulating || SelectedRuntimeMode == RuntimeMode.Simulation)
            return;
        _commBlackout = true;

        // 간트 actual 동결 — 진행 중 막대의 "무한 연장"(무지의 거짓 표시) 중단.
        // 동결 시각 = 마지막 신호 시각 (무소식 감지 지연만큼 막대가 더 길어지지 않게 wall 차이를 보정).
        var last = Interlocked.Read(ref _lastSignalWallTicks);
        var freezeAt = GanttChart.AdjustedNow;
        if (last > 0)
        {
            var sinceLast = TimeSpan.FromTicks(Math.Max(0, DateTime.Now.Ticks - last));
            freezeAt -= sinceLast;
        }
        // shadow coast — actual 동결 전에 직전 완료 사이클 템플릿을 떠서 두절 구간의
        // 추정 진행(간트 점선 틀)을 시작한다. 템플릿이 없으면(첫 사이클 중 두절) 공백이 정직.
        var coast = GanttChart.TryBeginShadowCoast(freezeAt);

        GanttChart.FreezeOpenSegments(freezeAt);

        // 진행 중 관측·측정 무효화 — 단절 시간이 포함된 elapsed/span 오염 차단 (학습 누적치는 보존).
        _simEngine?.InvalidateAbnormalObservations();
        _passiveInference?.InvalidateObservations();
        _durationLearning?.InvalidateAll();

        var coastNote = coast is { } w
            ? $"직전 사이클(주기 {w.PeriodMs / 1000.0:F1}s) 기반 추정 진행을 표시합니다."
            : "완료 사이클이 없어 추정 진행 표시를 생략합니다.";
        SimLog.Warn($"[CommBlackout] {reason} — actual 동결, abnormal 억제, 관측 무효화. {coastNote}");
        AddSimLog($"[통신 두절] {reason} — 신호 재개까지 이상 판정을 중단합니다. {coastNote}", LogSeverity.Warn);
    }

    private void ExitCommBlackout(string reason)
    {
        if (!_commBlackout) return;
        _commBlackout = false;

        // shadow coast 는 여기서 닫지 않는다 — 신호 재개 ≠ 위치 파악(사용자 확정).
        // 인퍼런스가 다시 유추해낸 첫 실측 Going(reconcile)까지 추정 진행(고스트)을 유지해
        // "재개 후 유추 복귀 전" 구간의 간트 공백을 막는다. 닫기는 TryReconcileShadowCoastOnResume.
        _shadowReconcilePending = GanttChart.ShadowWindows.Count > 0
            && GanttChart.ShadowWindows[^1].EndTime is null;

        // 재개 후 첫 Going 은 사이클 중간 합류 — plan 틀 시작점이 가짜라 행별 1회 생략
        // (안 하면 합류 틀이 다음 사이클까지 침범 — 실기 Monitoring 재합류에서 확인된 어긋남).
        GanttChart.SuppressNextGoingPlanOverlay();

        SimLog.Info($"[CommBlackout] 해제 ({reason}) — 관측 재개");
        AddSimLog($"[통신 재개] {reason} — 이상 판정을 재개합니다.", LogSeverity.System);
    }

    /// <summary>
    /// 재개 후 첫 실측 Call Going — shadow 추정 위치와 대조. 신뢰 윈도우(두절 1사이클당
    /// 주기 5% 누적) 안이면 무에러 합류, 벗어나면 추정 구간을 미확정으로 강등(간트에서 흐려짐).
    /// 해당 Call 이 템플릿에 없으면 판정 보류 — 다음 Going 으로 재시도.
    /// </summary>
    private void TryReconcileShadowCoastOnResume(Guid callEntryId, DateTime goingStart)
    {
        if (!_shadowReconcilePending || _commBlackout) return;

        // 고스트 닫기는 행별 자동(MarkShadowResumeForEntry — Going 세그먼트 기록 시) —
        // 여기서는 첫 실측 Going 의 위치 대조(reconcile) 판정만 1회 수행한다.
        var result = GanttChart.TryReconcileShadowCoast(callEntryId, goingStart);
        if (result is not { } r) return;   // 템플릿에 없는 entry — 판정만 다음 Going 으로 보류
        _shadowReconcilePending = false;

        if (r.Joined)
        {
            SimLog.Info($"[CommBlackout] reconcile 합류 — 오차 {r.ErrorMs:F0}ms (허용 {r.ToleranceMs:F0}ms)");
            AddSimLog($"[통신 재개] 재개 신호가 추정 위치 근방입니다 — 오차 {r.ErrorMs:F0}ms (허용 {r.ToleranceMs:F0}ms), 무에러 합류.", LogSeverity.System);
        }
        else
        {
            SimLog.Warn($"[CommBlackout] reconcile 불일치 — 오차 {r.ErrorMs:F0}ms > 허용 {r.ToleranceMs:F0}ms, 추정 구간 미확정 강등");
            AddSimLog($"[통신 재개] 재개 신호가 추정 위치와 어긋납니다 — 오차 {r.ErrorMs:F0}ms (허용 {r.ToleranceMs:F0}ms). 두절 구간 추정을 미확정으로 표시합니다.", LogSeverity.Warn);
        }
    }
}
