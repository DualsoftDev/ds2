using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Ds2.Core;
using Ds2.Store;
using Ds2.Editor;
using Promaker.Presentation;

namespace Promaker.ViewModels;

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
    public Guid? ParentWorkId { get; init; }
    public string SystemName { get; init; } = "";
    public int RowIndex { get; init; }
    public ObservableCollection<GanttStateSegment> Segments { get; } = [];

    private Status4 _currentState = Status4.Ready;
    public Status4 CurrentState
    {
        get => _currentState;
        set { _currentState = value; Notify(); }
    }

    public bool IsWork => Kind == EntityKind.Work;
    public bool IsCall => Kind == EntityKind.Call;
    public double YOffset { get; set; }
    public double RowHeight => 22;
    public int IndentLevel => IsCall ? 1 : 0;
    public Thickness IndentMargin => new(IndentLevel * 16, 0, 0, 0);
    public string DisplayName => IsCall ? $"\u2514 {Name}" : Name;

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
        set { _startTime = value; Notify(); Notify(nameof(TotalDuration)); }
    }

    private DateTime _currentTime = DateTime.Now;
    public DateTime CurrentTime
    {
        get => _currentTime;
        set { _currentTime = value; Notify(); Notify(nameof(TotalDuration)); Notify(nameof(ElapsedText)); }
    }

    public TimeSpan TotalDuration => CurrentTime - StartTime;
    public string ElapsedText => TotalDuration.ToString(@"hh\:mm\:ss\.f");

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

    /// <summary>Pause 누적 시간을 보정한 현재 시각 (Pause 중이면 고정)</summary>
    public DateTime AdjustedNow
    {
        get
        {
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

    public void UpdateNodeState(Guid nodeId, Status4 newState, DateTime timestamp)
    {
        var entry = FindEntry(nodeId);
        if (entry == null) return;

        bool isCall = entry.IsCall;
        bool shouldShowSegment = !isCall || newState == Status4.Going;

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
        }

        entry.CurrentState = newState;
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

    public GanttTimelineEntry AddEntry(Guid id, string name, EntityKind kind, Guid? parentWorkId = null, string systemName = "")
    {
        var entry = new GanttTimelineEntry
        {
            Id = id,
            Name = name,
            Kind = kind,
            ParentWorkId = parentWorkId,
            SystemName = systemName,
            RowIndex = Entries.Count
        };

        if (kind != EntityKind.Call)
        {
            entry.Segments.Add(new GanttStateSegment
            {
                State = Status4.Ready,
                StartTime = StartTime
            });
        }

        Entries.Add(entry);
        return entry;
    }
}
