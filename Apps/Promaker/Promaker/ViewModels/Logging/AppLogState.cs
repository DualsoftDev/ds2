using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;  // ICollectionView
using System.Threading;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using log4net.Core;
using Promaker.Presentation;
using Promaker.Services;

namespace Promaker.ViewModels.Logging;

/// <summary>
/// 앱 전역 log4net 출력의 GUI VM (singleton).
/// - 모든 Entries mutation 은 UI thread 의 flush 콜백에서만 → CollectionView 가 UI thread context 에 묶임.
/// - Append 호출 thread 가 worker 여도 _pending 큐 (_gate 보호) 에만 누적되고 16ms coalesce 후 dispatcher 로 marshal.
/// - 첫 인스턴스화는 반드시 UI thread 에서 — App.OnStartup 의 prefetch (_ = AppLogState.Instance) 가 보장.
/// </summary>
public sealed partial class AppLogState : ObservableObject
{
    /// <summary>App.OnStartup prefetch 가 UI thread 에서 강제 인스턴스화.</summary>
    public static AppLogState Instance { get; } = new();

    public ObservableCollection<AppLogEntry> Entries { get; } = new();
    public ICollectionView View { get; }

    /// <summary>필터 ComboBox 후보 — Enum.GetValues&lt;T&gt; generic 사용 (보일러 list 없음).</summary>
    public IReadOnlyList<LogLevelChoice> LevelChoices { get; } = Enum.GetValues<LogLevelChoice>();

    [ObservableProperty]
    private LogLevelChoice _selectedLevel;

    // GUI 가용 ring buffer 한계 — 초과분은 FIFO trim (UI thread flush 안에서 일괄).
    private const int MaxEntries = 5000;
    // App.Current null 인 startup 극초기에 누적되는 in-memory cap. 초과 시 oldest drop.
    private const int PreInitCap = 200;

    private readonly object _gate = new();
    private readonly Queue<AppLogEntry> _pending = new();
    private readonly Timer _flushTimer;
    private bool _flushScheduled;
    private long _seqCounter;

    private AppLogState()
    {
        // 디스크 → backing field 직접 (setter 우회 → OnSelectedLevelChanged 의 SaveEnum 재기록 회피).
        // View.Filter 클로저보다 먼저 로드해서 첫 호출 시점에 올바른 값이 반영되도록 명확화.
        _selectedLevel = AppSettingStore.LoadEnumOrDefault(
            SettingsPaths.LogFilterLevel, LogLevelChoice.Info);

        View = CollectionViewSource.GetDefaultView(Entries);
        View.Filter = o => Filter((AppLogEntry)o!, _selectedLevel);

        // dummy: 명시적 Change 호출 전엔 발화하지 않도록 Timeout.Infinite 로 시작.
        _flushTimer = new Timer(_ => OnTimerTick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>appender 가 호출 — Seq 단조 증가 보장.</summary>
    public long NextSeq() => Interlocked.Increment(ref _seqCounter);

    /// <summary>appender Append 진입점. 호출 thread 무관, _pending 누적 + 16ms coalesce.</summary>
    public void Enqueue(AppLogEntry entry)
    {
        lock (_gate)
        {
            _pending.Enqueue(entry);
            // App.Current 가 아직 null 인 극초기엔 marshal 할 수 없으므로 cap 내에서 누적만.
            while (_pending.Count > PreInitCap && Application.Current is null)
                _pending.Dequeue();

            if (_flushScheduled) return;
            _flushScheduled = true;
        }
        _flushTimer.Change(16, Timeout.Infinite);
    }

    private void OnTimerTick()
    {
        // dispatcher 가 살아있을 때만 marshal.
        if (Application.Current?.Dispatcher is { } d && !d.HasShutdownStarted)
        {
            d.BeginInvoke((Action)FlushOnUI);
            return;
        }

        // App.Current 가 아직 set 되지 않은 극초기 (prefetch 가 OnStartup 안에 있으므로 실 hit 확률 낮음).
        // _pending 이 비어있지 않으면 100ms 후 재시도 — Enqueue 추가 발생 없이도 큐가 굶지 않도록 보장.
        // shutdown 진행 중이면 정지 (RollingFile 엔 남음).
        bool hasPending;
        lock (_gate)
        {
            _flushScheduled = false;
            hasPending = _pending.Count > 0;
        }
        if (hasPending && Application.Current is null)
        {
            lock (_gate) { _flushScheduled = true; }
            _flushTimer.Change(100, Timeout.Infinite);
        }
    }

    private void FlushOnUI()
    {
        List<AppLogEntry> batch;
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                _flushScheduled = false;
                return;
            }
            batch = new List<AppLogEntry>(_pending.Count);
            while (_pending.Count > 0) batch.Add(_pending.Dequeue());
            _flushScheduled = false;
        }

        foreach (var e in batch) Entries.Add(e);

        // FIFO trim — 16ms batch 안에서 다발 RemoveAt.
        // 사용자가 trim 대상 entry 를 선택 중이면 SelectionChanged 가 발생할 수 있으나 (acceptable).
        while (Entries.Count > MaxEntries) Entries.RemoveAt(0);
    }

    public void Clear()
    {
        if (Application.Current?.Dispatcher is { } d && !d.CheckAccess())
        {
            d.BeginInvoke((Action)Clear);
            return;
        }
        // _pending 도 함께 비움 — 비우지 않으면 다음 16ms flush 가 누적분을 즉시 재등장시킴.
        lock (_gate)
        {
            _pending.Clear();
            _flushScheduled = false;
        }
        Entries.Clear();
    }

    partial void OnSelectedLevelChanged(LogLevelChoice value)
    {
        AppSettingStore.SaveEnum(SettingsPaths.LogFilterLevel, value);
        View.Refresh();
    }

    // log4net Level Value: DEBUG=30000, INFO=40000, WARN=60000, ERROR=70000, FATAL=110000.
    // 첫 항 (Error floor) 은 ERROR/FATAL 무조건 표시 의도 표명. SelectedLevel ∈ {Debug,Info,Warn} 한정에선
    // 둘째 항만으로도 포함되지만, 향후 후보 확장에 대비한 의도 고정.
    private static bool Filter(AppLogEntry e, LogLevelChoice selected) =>
        e.Level.Value >= Level.Error.Value
        || e.Level.Value >= ChoiceToLog4Net(selected).Value;

    private static Level ChoiceToLog4Net(LogLevelChoice c) => c switch
    {
        LogLevelChoice.Debug => Level.Debug,
        LogLevelChoice.Info  => Level.Info,
        LogLevelChoice.Warn  => Level.Warn,
        _                    => Level.Info,
    };
}
