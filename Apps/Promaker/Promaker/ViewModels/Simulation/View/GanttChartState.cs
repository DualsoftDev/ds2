using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Promaker.Presentation;

namespace Promaker.ViewModels;

/// <summary>간트 행 종류 — Work ▸ Call ▸ ApiCall(한 행이 Plan 위/I/O 아래 2줄)</summary>
public enum GanttRowKind { Work, Call, ApiCall }

/// <summary>간트차트 상태 세그먼트 — 하나의 상태 구간</summary>
public class GanttStateSegment : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private Status4 _state = Status4.Ready;
    public Status4 State
    {
        get => _state;
        set { _state = value; Notify(); Notify(nameof(StateBrush)); }
    }

    private DateTime _startTime;
    public DateTime StartTime
    {
        get => _startTime;
        set { _startTime = value; Notify(); Notify(nameof(Duration)); }
    }

    private DateTime? _endTime;
    public DateTime? EndTime
    {
        get => _endTime;
        set { _endTime = value; Notify(); Notify(nameof(Duration)); }
    }

    public TimeSpan Duration => (EndTime ?? DateTime.Now) - StartTime;

    /// <summary>이 Going 의 plan 틀 생략 — 통신 재개 후 첫 Going 같은 "중간 합류" 세그먼트는
    /// 시작점이 실제 사이클 시작이 아니라 합류 시점이라, 틀을 그리면 다음 사이클까지 침범한다.</summary>
    public bool SuppressPlanOverlay { get; set; }

    private bool _isAbnormal;
    /// <summary>이 사이클에 진짜 abnormal 판정(이벤트)이 떨어졌는가 — 바를 경고색으로.
    /// "벗어남=참고(plan 틀), 색=이상(판정)" 시각 언어 — 판정된 사이클 바만 칠한다.</summary>
    public bool IsAbnormal
    {
        get => _isAbnormal;
        set { if (_isAbnormal == value) return; _isAbnormal = value; Notify(); Notify(nameof(StateBrush)); }
    }

    public Brush StateBrush => _isAbnormal
        ? Status4Visuals.ResolveGanttBarAbnormalBrush()
        : Status4Visuals.ResolveGanttBarBrush(State);

    public string StateFullName => Status4Visuals.DisplayName(State);
}

/// <summary>간트차트 타임라인 항목 — 하나의 Work 또는 Call 행</summary>
public class GanttTimelineEntry : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public EntityKind Kind { get; init; } = EntityKind.Work;
    public GanttRowKind RowKind { get; init; } = GanttRowKind.Work;
    public Guid? ParentWorkId { get; init; }
    public Guid? ParentCallId { get; init; }
    public string SystemName { get; init; } = "";
    public int RowIndex { get; init; }
    /// <summary>plan 길이(ms) — PLAY 시 모델값으로 초기화, 라이브 학습 반영 시 갱신(set).</summary>
    public double? BaseDurationMs { get; set; }
    public int VirtualAppendMs { get; init; }
    public int OutputAppendMs { get; init; }
    /// <summary>ApiCall I/O \uc904 \u2014 \uc2e4\uc81c \uc1a1\uc2e0(Out)/\uc218\uc2e0(In) Tag \uc8fc\uc18c. OnHubTagChanged \ub9e4\ud551\uc6a9.</summary>
    public string OutAddress { get; init; } = "";
    public string InAddress { get; init; } = "";
    /// <summary>\uc717\uc904 \ub9c9\ub300 \u2014 Work/Call \uc0c1\ud0dc, ApiCall \uc740 Plan(\uacc4\ud68d) \uc904.</summary>
    public ObservableCollection<GanttStateSegment> Segments { get; } = [];
    /// <summary>\uc544\ub7ab\uc904 \ub9c9\ub300 \u2014 ApiCall \uc758 I/O(\uc2e4\uc81c Out\u00b7In) \uc904.</summary>
    public ObservableCollection<GanttStateSegment> OutSegments { get; } = [];
    /// <summary>아랫줄 막대 — device 실제 In(수신) high 구간. Out 과 독립 추적(겹쳐도 서로 안 닫힘).</summary>
    public ObservableCollection<GanttStateSegment> InSegments { get; } = [];

    private Status4 _currentState = Status4.Ready;
    public Status4 CurrentState
    {
        get => _currentState;
        set { _currentState = value; Notify(); }
    }

    public bool IsWork => RowKind == GanttRowKind.Work;
    public bool IsCall => RowKind == GanttRowKind.Call;
    public bool IsApiCall => RowKind == GanttRowKind.ApiCall;
    /// <summary>\uc811\uae30/\ud3b4\uae30 \ud1a0\uae00 \ub300\uc0c1 \u2014 Work/Call \ub9cc \uc790\uc2dd\uc744 \uac00\uc9c4\ub2e4.</summary>
    public bool HasChildren => IsWork || IsCall;
    public double YOffset { get; set; }
    /// <summary>ApiCall \uc740 Plan(\uc704)/I/O(\uc544\ub798) 2\uc904\uc774\ub77c \ub192\uc774 2\ubc30.</summary>
    public double RowHeight => IsApiCall ? 28 : 22;
    /// <summary>ApiCall \ud55c \uc904(Plan \ub610\ub294 I/O) \ub192\uc774.</summary>
    public double SubRowHeight => 14;
    public int IndentLevel => RowKind switch
    {
        GanttRowKind.Call => 1,
        GanttRowKind.ApiCall => 2,
        _ => 0
    };
    public Thickness IndentMargin => new(IndentLevel * 14, 0, 0, 0);
    public string DisplayName => RowKind switch
    {
        GanttRowKind.ApiCall => $"\u2514 {Name}",   // PLN/I/O \ud45c\uc2dd\uc740 XAML \uc6b0\uce21 \uce7c\ub7fc\uc5d0 \uc138\ub85c\ub85c
        _ => Name   // Work/Call \uc740 \uc774\ub984\ub9cc (Call \uc758 \u2514 \uc81c\uac70)
    };

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; Notify(); }
    }

    private bool _isVisible = true;
    public bool IsVisible
    {
        get => _isVisible;
        set { _isVisible = value; Notify(); }
    }

}

/// <summary>shadow coast 템플릿의 Going 하나 — 사이클 시작 기준 offset 과 길이(ms).</summary>
public readonly record struct ShadowTemplateGoing(double OffsetMs, double DurationMs);

/// <summary>shadow coast 추정 막대(렌더 산출물) — entry 행에 그릴 절대 시각 구간.</summary>
public readonly record struct ShadowBar(Guid EntryId, DateTime StartTime, DateTime EndTime);

/// <summary>shadow coast reconcile 판정 — 재개 신호가 추정 위치 근방인지.</summary>
public readonly record struct ShadowReconcileResult(bool Joined, double ErrorMs, double ToleranceMs);

/// <summary>
/// 통신 두절(blackout) 한 구간의 shadow coast — actual 은 동결되지만 plan(추정)은 계속 가야 한다.
/// 직전 완료 사이클에서 entry 별 Going 타이밍 템플릿을 떠서 두절 구간에 주기 반복으로 투영한다
/// (내비 터널 구간의 추측 항법). 세그먼트 데이터에는 섞지 않고 렌더 레이어에서만 그린다 —
/// 추정이 실측 기록을 오염시키지 않는다.
/// </summary>
public class GanttShadowCoastWindow
{
    /// <summary>두절(freeze) 시각 — 추정 표시 시작.</summary>
    public DateTime StartTime { get; init; }
    /// <summary>전역 종료 시각 — null 이면 추정 진행 중(현재 시각까지 자람).
    /// 행별 종료는 <see cref="ResumeByEntry"/> 가 우선 — 모든 행이 복귀하면 여기도 닫힌다.</summary>
    public DateTime? EndTime { get; set; }
    /// <summary>행별 유추 복귀 시각 — 그 행의 첫 실측 Going. 고스트는 행마다 자기 복귀까지 유지
    /// (한 행의 복귀가 다른 행의 추정을 끊으면 늦게 복귀하는 행에 공백이 생긴다 — 실기 확인).</summary>
    public Dictionary<Guid, DateTime> ResumeByEntry { get; } = [];
    /// <summary>직전 완료 사이클 주기(연속 anchor Work Going 시작 간격).</summary>
    public double PeriodMs { get; init; }
    /// <summary>두절 시점 사이클의 시작(anchor Work 의 마지막 Going 시작) — 템플릿 k=0 위상 기준.</summary>
    public DateTime AnchorCycleStart { get; init; }
    /// <summary>entry 별 직전 완료 사이클의 Going 타이밍.</summary>
    public Dictionary<Guid, List<ShadowTemplateGoing>> Template { get; init; } = [];
    /// <summary>reconcile 불일치 — 재개 신호가 추정과 어긋남, 추정 구간을 미확정(흐리게) 강등.</summary>
    public bool LowConfidence { get; set; }
    /// <summary>재개 후 첫 Going 과 비교 완료 여부.</summary>
    public bool Reconciled { get; set; }
}

/// <summary>간트차트 전체 뷰모델</summary>
public class GanttChartState : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public ObservableCollection<GanttTimelineEntry> Entries { get; } = [];

    private DateTime _startTime = DateTime.Now;
    public DateTime StartTime
    {
        get => _startTime;
        set { _startTime = value; Notify(); Notify(nameof(TotalDuration)); Notify(nameof(TimelineDuration)); }
    }

    private DateTime _currentTime = DateTime.Now;
    public DateTime CurrentTime
    {
        get => _currentTime;
        set { _currentTime = value; Notify(); Notify(nameof(TotalDuration)); Notify(nameof(TimelineDuration)); Notify(nameof(ElapsedText)); }
    }

    public TimeSpan TotalDuration => CurrentTime - StartTime;
    public string ElapsedText => TotalDuration.ToString(@"hh\:mm\:ss\.f");

    /// <summary>표시 윈도우 길이(분) — 빨간 타임라인(현재시각) 기준 최근 N분만 간트에 보인다. PLC 설정
    /// 슬라이더로 5분~300분(5시간) 조정. 그보다 오래된 구간은 링버퍼 트림으로 스크롤해도 닿지 않는다.</summary>
    public int RenderWindowMinutes { get; set; } = 300;

    /// <summary>
    /// 렌더 origin(보이는 왼쪽 끝) — 빨간 타임라인(CurrentTime) 기준 최근 RenderWindowMinutes 분만 보이는
    /// 슬라이딩 윈도우의 시작점. origin = max(StartTime, CurrentTime - N분). 운전이 N분 미만이면 세션
    /// 시작부터(윈도우 아직 안 참), N분 넘으면 항상 최근 N분만 — 빨간선이 움직이면 윈도우도 같이 슬라이딩
    /// (예: 빨간선 14:00:00·윈도우 5h → 왼쪽 끝 9:00:00, 0~9h 는 스크롤해도 안 보임). X축 라벨은
    /// RenderTimeRuler 가 (origin - StartTime) 을 더해 세션 시작 기준 경과시각으로 표기(왼쪽 끝 9:00:00
    /// 식 — 0s 아님). 총 가동시간(ElapsedText)은 StartTime 기준 — 분리.
    /// </summary>
    public DateTime RenderStartTime
    {
        get
        {
            var cutoff = CurrentTime.AddMinutes(-RenderWindowMinutes);
            return cutoff < StartTime ? StartTime : cutoff;
        }
    }

    public TimeSpan TimelineDuration => GetTimelineEndTime() - RenderStartTime;

    private double _pixelsPerSecond = 50.0;
    public double PixelsPerSecond
    {
        get => _pixelsPerSecond;
        set { _pixelsPerSecond = Math.Clamp(value, MinPixelsPerSecond, MaxPixelsPerSecond); Notify(); }
    }

    public const double MinPixelsPerSecond = 5.0;
    public const double MaxPixelsPerSecond = 500.0;

    private double _horizontalOffset;
    public double HorizontalOffset
    {
        get => _horizontalOffset;
        set { _horizontalOffset = Math.Max(0, value); Notify(); }
    }

    private double _verticalOffset;
    public double VerticalOffset
    {
        get => _verticalOffset;
        set { _verticalOffset = Math.Max(0, value); Notify(); }
    }

    private bool _isRunning;
    private DateTime _pausedAt;
    private TimeSpan _totalPausedDuration = TimeSpan.Zero;

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (_isRunning == value) return;
            if (!value)
                _pausedAt = DateTime.Now;
            else if (_pausedAt != default)
                _totalPausedDuration += DateTime.Now - _pausedAt;
            _isRunning = value;
            Notify();
        }
    }

    /// <summary>
    /// 현재 시각 source override.
    /// Simulation 모드에서는 sim clock 기반 (`_simStartTime + engine.State.Clock`) 으로 set 되어
    /// 빨간선과 노드 막대가 같은 시간 기준으로 일치. 그 외 모드 (Control/VP/Monitoring) 는 null —
    /// wall clock 기반 default 사용 (외부 신호 owner 라 sim clock 이 wall clock 따라가지 못함).
    /// </summary>
    public Func<DateTime>? NowOverride { get; set; }

    /// <summary>
    /// 열린(진행 중) 세그먼트의 렌더 끝 상한 — "증거(마지막 신호) 시각" provider.
    /// 신호 기반 모드에서 통신이 멎으면 blackout 확정(무소식 3초) 전에도 열린 바가
    /// 빨간 선까지 계속 자라다 동결 시 마지막 신호 시각으로 "챡" 되감기는 왜곡이 생긴다 —
    /// 렌더가 매 프레임 이 cap 으로 클립하면 바가 증거가 있는 데까지만 자란다.
    /// null(provider 없음/반환 null) 이면 기존 동작(현재 시각까지).
    /// </summary>
    public Func<DateTime?>? OpenSegmentEvidenceCap { get; set; }

    /// <summary>
    /// plan vs actual overlay 표시 여부 — 비-Simulation 모드(Control/Monitoring/VP)에서 true.
    /// Work/Call 행의 Going 구간에 plan(BaseDurationMs) 배경 바를 행 높이로 깔고,
    /// actual 상태 막대는 얇게 가운데 그린다. actual 이 배경보다 짧으면 빠른 것, 뚫고 나가면 느린 것.
    /// </summary>
    private bool _showPlanOverlay;
    public bool ShowPlanOverlay
    {
        get => _showPlanOverlay;
        set { if (_showPlanOverlay == value) return; _showPlanOverlay = value; Notify(); }
    }

    /// <summary>
    /// 신호 유추 모드(VP/Monitoring) — 워밍업 후 사이클 "중간"에 합류한 첫 Going 세그먼트는
    /// 시작점이 실제 사이클 시작이 아니라 유추 확정 시점이라, plan 틀을 그리면 다음 사이클까지
    /// 침범한다. true 면 entry 별 첫 Going 의 plan 틀을 생략하고 둘째 사이클부터 그린다.
    /// </summary>
    private bool _suppressFirstGoingPlanOverlay;
    public bool SuppressFirstGoingPlanOverlay
    {
        get => _suppressFirstGoingPlanOverlay;
        set { if (_suppressFirstGoingPlanOverlay == value) return; _suppressFirstGoingPlanOverlay = value; Notify(); }
    }

    /// <summary>Pause 누적 시간을 보정한 현재 시각 (Pause 중이면 고정).
    /// NowOverride 가 set 되어 있으면 그 provider 가 우선 — sim clock 기반에서는
    /// engine.Pause 시 sim clock 자체가 멈추므로 별도 _pausedAt 보정 불필요.</summary>
    public DateTime AdjustedNow
    {
        get
        {
            if (NowOverride is { } provider)
                return provider();
            if (!_isRunning && _pausedAt != default)
                return _pausedAt - _totalPausedDuration;
            return DateTime.Now - _totalPausedDuration;
        }
    }

    public void Reset(DateTime startTime)
    {
        StartTime = startTime;
        CurrentTime = startTime;
        _totalPausedDuration = TimeSpan.Zero;
        _pausedAt = default;
        Entries.Clear();
        ShadowWindows.Clear();
        _suppressNextGoingPlan.Clear();
        HorizontalOffset = 0;
        VerticalOffset = 0;
    }

    /// <summary>
    /// entry 당 보관할 최대 segment 수. VP/Control 같이 외부 신호로 장시간 가동되는 모드에서
    /// 무한 Add 시 OOM 으로 이어지므로 가장 오래된 segment 부터 잘라낸다.
    /// </summary>
    public const int MaxSegmentsPerEntry = 2000;

    public void UpdateNodeState(Guid nodeId, Status4 newState, DateTime timestamp)
    {
        var entry = FindEntry(nodeId);
        if (entry == null) return;

        ApplyStateSegment(entry, newState, timestamp);

        // Call 의 Plan 줄(=ApiCall 윗줄) = Call 수명(plan duration) 동기화.
        if (entry.IsCall)
        {
            foreach (var e in Entries)
                if (e.IsApiCall && e.ParentCallId == nodeId)
                    ApplyStateSegment(e, newState, timestamp);
        }

        CurrentTime = timestamp;
    }

    private void ApplyStateSegment(GanttTimelineEntry entry, Status4 newState, DateTime timestamp)
    {
        // Call/Plan 줄은 Going 구간만 막대로 그린다 (Ready/Finish/Homing 은 빈 구간).
        // R/G/F/H 전부 막대로 표기 (Ready 포함) — 색은 Status4Visuals.GanttBar{Ready=초록/Going=주황/Finish=파랑/Homing=회색}.
        bool shouldShowSegment = true;

        var lastSegment = entry.Segments.Count > 0 ? entry.Segments[^1] : null;
        if (lastSegment is { EndTime: null })
            lastSegment.EndTime = timestamp;

        if (shouldShowSegment)
        {
            entry.Segments.Add(new GanttStateSegment
            {
                State = newState,
                StartTime = timestamp,
                // 통신 재개 후 첫 Going = 사이클 중간 합류 — 시작점이 가짜라 plan 틀 생략.
                SuppressPlanOverlay = newState == Status4.Going && _suppressNextGoingPlan.Remove(entry.Id)
            });
            while (entry.Segments.Count > MaxSegmentsPerEntry)
                entry.Segments.RemoveAt(0);

            // shadow coast 행별 유추 복귀 — 이 행의 첫 실측 Going 이 곧 "위치를 다시 안" 시점.
            // 고스트는 행마다 자기 복귀까지 유지된다 (Call/Work/ApiCall PLN 줄 모두 이 경로).
            if (newState == Status4.Going)
                MarkShadowResumeForEntry(entry.Id, timestamp);
        }

        entry.CurrentState = newState;
    }

    /// <summary>열린 shadow 윈도우에 행별 유추 복귀 시각 기록(최초 1회) — 그 행의 고스트가 여기서 끝난다.
    /// 템플릿의 모든 행이 복귀하면 윈도우를 전역 마감한다(다음 두절이 새 윈도우를 열 수 있게).</summary>
    private void MarkShadowResumeForEntry(Guid entryId, DateTime at)
    {
        if (ShadowWindows.Count == 0) return;
        var window = ShadowWindows[^1];
        if (window.EndTime is not null) return;
        if (!window.Template.ContainsKey(entryId) || window.ResumeByEntry.ContainsKey(entryId)) return;

        window.ResumeByEntry[entryId] = at;
        if (window.ResumeByEntry.Count >= window.Template.Count)
        {
            var last = window.StartTime;
            foreach (var t in window.ResumeByEntry.Values)
                if (t > last) last = t;
            window.EndTime = last;
        }
    }

    /// <summary>통신 재개 — 전 행의 "다음 Going" plan 틀을 1회 생략 예약. 두절 후 첫 Going 은
    /// 실제 사이클 시작이 아니라 합류 시점에서 시작하므로(신호 유추 워밍업 합류와 같은 문제)
    /// 틀을 그리면 다음 사이클을 침범한다. 둘째 사이클부터 정상 틀.</summary>
    public void SuppressNextGoingPlanOverlay()
    {
        foreach (var e in Entries)
            _suppressNextGoingPlan.Add(e.Id);
    }

    private readonly HashSet<Guid> _suppressNextGoingPlan = new();

    /// <summary>실제 I/O — Tag(Out·In) 변화를 해당 ApiCall I/O 줄의 막대로. on=high 구간 시작, off=종료.</summary>
    public void UpdateIoState(string address, bool isOn, DateTime timestamp)
    {
        if (string.IsNullOrEmpty(address)) return;
        foreach (var e in Entries)
        {
            if (!e.IsApiCall) continue;
            bool isOut = e.OutAddress == address;
            bool isIn = e.InAddress == address;
            if (!isOut && !isIn) continue;

            // Out/In 을 독립 리스트로 추적 — off 신호가 같은 종류(Out↔Out, In↔In)의 직전 high 구간만 닫는다.
            //   단일 리스트로 섞으면 Out off 가 In segment 를(또는 그 반대) 잘못 닫아 짧은 조각 + 빨간선 중복이
            //   생긴다(출력 유지 중 In 이 들어와 Out·In high 구간이 겹칠 수 있으므로).
            var segs = isOut ? e.OutSegments : e.InSegments;
            var last = segs.Count > 0 ? segs[^1] : null;
            if (last is { EndTime: null })
                last.EndTime = timestamp;

            if (isOn)
            {
                segs.Add(new GanttStateSegment
                {
                    State = isOut ? Status4.Going : Status4.Finish,
                    StartTime = timestamp
                });
                while (segs.Count > MaxSegmentsPerEntry)
                    segs.RemoveAt(0);
            }
        }
        CurrentTime = timestamp;
    }

    public void SyncNodeState(Guid nodeId, Status4 currentState, DateTime timestamp)
    {
        var entry = FindEntry(nodeId);
        if (entry == null) return;

        if (entry.CurrentState != currentState)
        {
            UpdateNodeState(nodeId, currentState, timestamp);
            return;
        }

        CurrentTime = timestamp;
    }

    /// <summary>abnormal 판정 — 해당 entry(Call 이면 자식 ApiCall 행 포함)의 최근 Going 사이클 바를
    /// 경고색으로 마킹. plan 틀 벗어남은 색을 바꾸지 않는다(참고) — 색이 바뀌는 건 판정뿐.</summary>
    public void MarkAbnormal(Guid nodeId)
    {
        var entry = FindEntry(nodeId);
        if (entry is null) return;
        MarkLatestGoingAbnormal(entry);
        if (entry.IsCall)
        {
            foreach (var e in Entries)
                if (e.IsApiCall && e.ParentCallId == nodeId)
                    MarkLatestGoingAbnormal(e);
        }
    }

    private static void MarkLatestGoingAbnormal(GanttTimelineEntry entry)
    {
        // 판정 시점의 사이클 = 가장 최근 Going (진행 중이거나 방금 닫힘 — SensorShort 는 Ready 중에도
        // 떨어질 수 있어 "직전 Going 사이클" 귀속이 자연스럽다).
        for (var i = entry.Segments.Count - 1; i >= 0; i--)
        {
            if (entry.Segments[i].State != Status4.Going) continue;
            entry.Segments[i].IsAbnormal = true;
            return;
        }
    }

    /// <summary>통신 blackout — 모든 행의 열린 세그먼트(상태·I/O)를 두절 시각으로 닫는다.
    /// 두절 구간은 증거가 없는데 진행 중 막대가 현재 시각까지 계속 늘어나면
    /// "마지막으로 알던 상태의 무한 연장" = 거짓 표시가 된다. actual 은 여기서 멈추고,
    /// plan 틀(예측)만 남는 것이 coast 구간의 올바른 시각 언어다.</summary>
    public void FreezeOpenSegments(DateTime at)
    {
        foreach (var entry in Entries)
        {
            CloseOpenSegment(entry.Segments, at);
            CloseOpenSegment(entry.OutSegments, at);
            CloseOpenSegment(entry.InSegments, at);
        }
    }

    private static void CloseOpenSegment(ObservableCollection<GanttStateSegment> segments, DateTime at)
    {
        if (segments.Count == 0) return;
        var last = segments[^1];
        if (last.EndTime is null && at > last.StartTime)
            last.EndTime = at;
    }

    // ── shadow coast — 두절 구간 plan 추정 진행 (직전 완료 사이클 템플릿의 주기 타일링) ──

    /// <summary>두절 이력 — 닫힌 윈도우도 보관해 재개 후에도 소실 구간의 추정 틀이 남는다.</summary>
    public List<GanttShadowCoastWindow> ShadowWindows { get; } = [];

    /// <summary>주기로 인정할 최소값(ms) — 이보다 짧으면 사이클이 아니라 노이즈.</summary>
    internal const double ShadowMinPeriodMs = 100;

    /// <summary>
    /// 두절 진입 — 직전 완료 사이클에서 타이밍 템플릿을 떠 shadow coast 윈도우를 연다.
    /// anchor = 마지막 Going 시작이 가장 최근인 Work. 그 직전 Going 과의 시작 간격이 주기.
    /// 완료 사이클이 아직 없으면(첫 사이클 중 두절) null — 추정 근거가 없으니 공백이 정직하다.
    /// </summary>
    public GanttShadowCoastWindow? TryBeginShadowCoast(DateTime freezeAt)
    {
        // 직전 윈도우가 아직 열려 있으면(일부 행 미복귀 중 재두절) 여기서 전역 마감 —
        // 복귀했던 행의 actual 구간 위에 옛 추정이 다시 겹치지 않게 새 윈도우로 구간을 나눈다.
        if (ShadowWindows.Count > 0 && ShadowWindows[^1].EndTime is null)
            ShadowWindows[^1].EndTime = freezeAt > ShadowWindows[^1].StartTime
                ? freezeAt
                : ShadowWindows[^1].StartTime;

        GanttTimelineEntry? anchor = null;
        int anchorLastIdx = -1;
        foreach (var e in Entries)
        {
            if (!e.IsWork) continue;
            for (var i = e.Segments.Count - 1; i >= 0; i--)
            {
                if (e.Segments[i].State != Status4.Going) continue;
                if (anchorLastIdx < 0 || e.Segments[i].StartTime > anchor!.Segments[anchorLastIdx].StartTime)
                {
                    anchor = e;
                    anchorLastIdx = i;
                }
                break;
            }
        }
        if (anchor is null) return null;

        var anchorLast = anchor.Segments[anchorLastIdx];
        GanttStateSegment? prev = null;
        for (var i = anchorLastIdx - 1; i >= 0; i--)
        {
            if (anchor.Segments[i].State != Status4.Going) continue;
            prev = anchor.Segments[i];
            break;
        }
        if (prev is null) return null;   // 완료 사이클 없음

        var periodMs = (anchorLast.StartTime - prev.StartTime).TotalMilliseconds;
        if (periodMs < ShadowMinPeriodMs) return null;

        // 템플릿 — 직전 완료 사이클 [prev.Start, anchorLast.Start) 구간의 모든 entry Going.
        var template = new Dictionary<Guid, List<ShadowTemplateGoing>>();
        foreach (var e in Entries)
        {
            foreach (var s in e.Segments)
            {
                if (s.State != Status4.Going) continue;
                if (s.StartTime < prev.StartTime || s.StartTime >= anchorLast.StartTime) continue;
                var offset = (s.StartTime - prev.StartTime).TotalMilliseconds;
                var dur = ((s.EndTime ?? anchorLast.StartTime) - s.StartTime).TotalMilliseconds;
                if (dur <= 0) continue;
                if (!template.TryGetValue(e.Id, out var list))
                    template[e.Id] = list = [];
                list.Add(new ShadowTemplateGoing(offset, dur));
            }
        }
        if (template.Count == 0) return null;

        var window = new GanttShadowCoastWindow
        {
            StartTime = freezeAt,
            PeriodMs = periodMs,
            AnchorCycleStart = anchorLast.StartTime,
            Template = template
        };
        ShadowWindows.Add(window);
        return window;
    }

    /// <summary>신호 재개 — 열린 shadow 윈도우를 닫는다. 닫은 윈도우가 있으면 true(reconcile 대기 신호).</summary>
    public bool EndShadowCoast(DateTime at)
    {
        if (ShadowWindows.Count == 0) return false;
        var last = ShadowWindows[^1];
        if (last.EndTime is not null) return false;
        last.EndTime = at > last.StartTime ? at : last.StartTime;
        return true;
    }

    /// <summary>
    /// 재개 후 첫 실측 Going 을 shadow 추정 위치와 대조 — 신뢰 윈도우(끊김이 길수록 허용 오차 누적) 안이면
    /// 무에러 합류, 벗어나면 추정 구간을 미확정(LowConfidence)으로 강등한다.
    /// 해당 entry 가 템플릿에 없으면 null — 판정 보류(다음 Going 으로 재시도).
    /// </summary>
    public ShadowReconcileResult? TryReconcileShadowCoast(Guid entryId, DateTime actualGoingStart)
    {
        // 행별 복귀 정책에선 첫 실측 Going 시점에 윈도우가 아직 열려 있을 수 있다(미복귀 행 잔존) —
        // 미reconcile 이면 열림/닫힘 무관 대상. blackout 경과는 EndTime ?? actualGoingStart 로 계산.
        GanttShadowCoastWindow? window = null;
        for (var i = ShadowWindows.Count - 1; i >= 0; i--)
        {
            if (!ShadowWindows[i].Reconciled)
            {
                window = ShadowWindows[i];
                break;
            }
        }
        if (window is null) return null;
        if (!window.Template.TryGetValue(entryId, out var goings) || goings.Count == 0) return null;

        // 예측 Going 시작들(offset + k·P) 중 actual 에 가장 가까운 것과의 원형 거리.
        var bestErrorMs = double.MaxValue;
        var elapsedMs = (actualGoingStart - window.AnchorCycleStart).TotalMilliseconds;
        foreach (var g in goings)
        {
            var k = Math.Max(0, Math.Round((elapsedMs - g.OffsetMs) / window.PeriodMs));
            for (var kk = Math.Max(0, k - 1); kk <= k + 1; kk++)
            {
                var err = Math.Abs(elapsedMs - (g.OffsetMs + kk * window.PeriodMs));
                if (err < bestErrorMs) bestErrorMs = err;
            }
        }

        // 신뢰 윈도우 — 두절 1사이클당 주기의 5% 씩 허용 오차 누적(바닥 300ms).
        var blackoutMs = ((window.EndTime ?? actualGoingStart) - window.StartTime).TotalMilliseconds;
        var blackoutCycles = Math.Max(1.0, blackoutMs / window.PeriodMs);
        var toleranceMs = Math.Max(300.0, window.PeriodMs * 0.05 * blackoutCycles);

        window.Reconciled = true;
        var joined = bestErrorMs <= toleranceMs;
        window.LowConfidence = !joined;
        return new ShadowReconcileResult(joined, bestErrorMs, toleranceMs);
    }

    /// <summary>
    /// 렌더용 — 윈도우의 추정 막대를 절대 시각 구간으로 열거. 템플릿을 AnchorCycleStart 부터
    /// 주기 반복으로 투영하고 [윈도우 시작, min(행별 복귀, 전역 끝, until)] 로 클립한다.
    /// 행별 복귀(ResumeByEntry)가 우선 — 각 행의 고스트는 그 행이 다시 유추될 때까지 유지되고,
    /// 미복귀 행은 until(현재 시각)까지 실시간으로 자란다.
    /// </summary>
    public static IEnumerable<ShadowBar> EnumerateShadowBars(GanttShadowCoastWindow window, DateTime until)
    {
        var globalEnd = window.EndTime is { } closed && closed < until ? closed : until;
        if (globalEnd <= window.StartTime) yield break;

        foreach (var (entryId, goings) in window.Template)
        {
            var end = window.ResumeByEntry.TryGetValue(entryId, out var resumed) && resumed < globalEnd
                ? resumed
                : globalEnd;
            if (end <= window.StartTime) continue;

            foreach (var g in goings)
            {
                for (var k = 0; k < 10_000; k++)
                {
                    var start = window.AnchorCycleStart.AddMilliseconds(g.OffsetMs + k * window.PeriodMs);
                    if (start >= end) break;
                    var stop = start.AddMilliseconds(g.DurationMs);
                    var clipStart = start < window.StartTime ? window.StartTime : start;
                    var clipStop = stop > end ? end : stop;
                    if (clipStop > clipStart)
                        yield return new ShadowBar(entryId, clipStart, clipStop);
                }
            }
        }
    }

    public GanttTimelineEntry? FindEntry(Guid nodeId)
    {
        foreach (var entry in Entries)
        {
            if (entry.Id == nodeId)
                return entry;
        }
        return null;
    }

    private DateTime GetTimelineEndTime()
    {
        var end = CurrentTime;
        foreach (var entry in Entries)
        {
            foreach (var segment in entry.Segments)
            {
                var segmentEnd = segment.EndTime ?? CurrentTime;
                if (segmentEnd > end) end = segmentEnd;

                if (entry.OutputAppendMs > 0 && segment.State == Status4.Going && segment.EndTime is { } finishedAt)
                {
                    var outputEnd = finishedAt.AddMilliseconds(entry.OutputAppendMs);
                    if (outputEnd > end) end = outputEnd;
                }
            }
        }
        return end;
    }

    public GanttTimelineEntry AddEntry(
        Guid id,
        string name,
        EntityKind kind,
        Guid? parentWorkId = null,
        string systemName = "",
        double? baseDurationMs = null,
        int virtualAppendMs = 0,
        int outputAppendMs = 0)
    {
        var entry = new GanttTimelineEntry
        {
            Id = id,
            Name = name,
            Kind = kind,
            RowKind = kind == EntityKind.Call ? GanttRowKind.Call : GanttRowKind.Work,
            ParentWorkId = parentWorkId,
            SystemName = systemName,
            BaseDurationMs = baseDurationMs,
            VirtualAppendMs = Math.Max(0, virtualAppendMs),
            OutputAppendMs = Math.Max(0, outputAppendMs),
            RowIndex = Entries.Count,
            IsExpanded = kind != EntityKind.Call   // Call 기본 접힘(자식 ApiCall 숨김), Work 는 펼침
        };

        // 모든 행(Work/Call) 초기 Ready segment로 시작 — R/G/F/H 전부 간트에 표기.
        entry.Segments.Add(new GanttStateSegment
        {
            State = Status4.Ready,
            StartTime = StartTime
        });

        Entries.Add(entry);
        return entry;
    }

    /// <summary>Call 밑 ApiCall 행 추가 — 한 행이 Plan(윗줄)/I/O(아랫줄) 2줄.</summary>
    public GanttTimelineEntry AddApiCallEntry(
        Guid apiCallId,
        string name,
        Guid parentWorkId,
        Guid parentCallId,
        string systemName = "",
        double? baseDurationMs = null,
        string outAddress = "",
        string inAddress = "",
        int outputAppendMs = 0)
    {
        var entry = new GanttTimelineEntry
        {
            Id = apiCallId,
            Name = name,
            Kind = EntityKind.Call,
            RowKind = GanttRowKind.ApiCall,
            ParentWorkId = parentWorkId,
            ParentCallId = parentCallId,
            SystemName = systemName,
            BaseDurationMs = baseDurationMs,
            OutAddress = outAddress,
            InAddress = inAddress,
            OutputAppendMs = Math.Max(0, outputAppendMs),   // device I/O 줄(Out 끝)에 timeAppend 빨간 점선 표기용
            RowIndex = Entries.Count
        };
        entry.Segments.Add(new GanttStateSegment { State = Status4.Ready, StartTime = StartTime });
        Entries.Add(entry);
        return entry;
    }

    /// <summary>Work/Call 접기-펴기 토글 → 자식 행 IsVisible 갱신.</summary>
    public void SetExpanded(Guid entryId, bool expanded)
    {
        var target = FindEntry(entryId);
        if (target is null || !target.HasChildren) return;
        target.IsExpanded = expanded;
        RefreshVisibility();
    }

    /// <summary>특정 RowKind(Work/Call) 전체 접기-펴기 — 우클릭 메뉴용.</summary>
    public void ExpandAll(GanttRowKind kind, bool expanded)
    {
        foreach (var e in Entries)
            if (e.RowKind == kind && e.HasChildren)
                e.IsExpanded = expanded;
        RefreshVisibility();
    }

    /// <summary>전체 행 가시성 재계산 — 부모(Work→Call) 의 IsExpanded 사슬을 따른다.</summary>
    public void RefreshVisibility()
    {
        foreach (var e in Entries)
        {
            bool visible = true;
            if (e.RowKind == GanttRowKind.Call)
            {
                var work = e.ParentWorkId.HasValue ? FindEntry(e.ParentWorkId.Value) : null;
                visible = work?.IsExpanded ?? true;
            }
            else if (e.IsApiCall)
            {
                var call = e.ParentCallId.HasValue ? FindEntry(e.ParentCallId.Value) : null;
                var work = e.ParentWorkId.HasValue ? FindEntry(e.ParentWorkId.Value) : null;
                visible = (call?.IsExpanded ?? true) && (work?.IsExpanded ?? true);
            }
            e.IsVisible = visible;
        }
    }
}
