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

    public Brush StateBrush => Status4Visuals.ResolveGanttBarBrush(State);

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
    public double? BaseDurationMs { get; init; }
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
    public TimeSpan TimelineDuration => GetTimelineEndTime() - StartTime;

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
                StartTime = timestamp
            });
            while (entry.Segments.Count > MaxSegmentsPerEntry)
                entry.Segments.RemoveAt(0);
        }

        entry.CurrentState = newState;
    }

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
