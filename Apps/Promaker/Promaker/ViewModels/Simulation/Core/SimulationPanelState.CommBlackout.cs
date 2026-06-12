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
    /// <summary>신호 무소식 → blackout 진입 임계(ms). 디바이스 신호 간 간격(수백 ms)과
    /// 사이클 전환 갭(~1s)보다 충분히 큰 고정값 — 학습 기반 동적 임계는 후속.</summary>
    internal const int CommSilenceTimeoutMs = 3000;

    private volatile bool _commBlackout;
    internal bool IsCommBlackout => _commBlackout;
    private long _lastSignalWallTicks;   // hub 스레드가 Interlocked 로 갱신
    private DispatcherTimer? _commWatchdogTimer;

    /// <summary>ctor 말미에서 1회 — Hub 신호/PLC 상태 구독 + 무소식 워치독 시작.</summary>
    private void InitCommBlackoutWatch()
    {
        Hub.TagBroadcast += (_, _, _) => OnCommSignalObserved();
        Hub.PlcConnectionStatusChanged += OnCommPlcStatus;
        _commWatchdogTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _commWatchdogTimer.Tick += (_, _) => OnCommWatchdogTick();
        _commWatchdogTimer.Start();
    }

    /// <summary>PLAY/RESET 시 — blackout 해제 + 신호 시각 초기화(첫 신호 전 무소식 오탐 방지).</summary>
    private void ResetCommBlackout()
    {
        _commBlackout = false;
        Interlocked.Exchange(ref _lastSignalWallTicks, 0);
    }

    private void OnCommSignalObserved()
    {
        Interlocked.Exchange(ref _lastSignalWallTicks, DateTime.Now.Ticks);
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
        if (silenceMs > CommSilenceTimeoutMs)
            EnterCommBlackout($"신호 무소식 {silenceMs / 1000.0:F1}s");
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
        GanttChart.FreezeOpenSegments(freezeAt);

        // 진행 중 관측·측정 무효화 — 단절 시간이 포함된 elapsed/span 오염 차단 (학습 누적치는 보존).
        _simEngine?.InvalidateAbnormalObservations();
        _passiveInference?.InvalidateObservations();
        _durationLearning?.InvalidateAll();

        SimLog.Warn($"[CommBlackout] {reason} — actual 동결, abnormal 억제, 관측 무효화");
        AddSimLog($"[통신 두절] {reason} — 신호 재개까지 이상 판정을 중단합니다.", LogSeverity.Warn);
    }

    private void ExitCommBlackout(string reason)
    {
        if (!_commBlackout) return;
        _commBlackout = false;
        SimLog.Info($"[CommBlackout] 해제 ({reason}) — 관측 재개");
        AddSimLog($"[통신 재개] {reason} — 이상 판정을 재개합니다.", LogSeverity.System);
    }
}
