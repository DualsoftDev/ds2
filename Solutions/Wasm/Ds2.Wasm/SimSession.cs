using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Runtime.Engine;
using Ds2.Runtime.Model;

namespace Ds2.Wasm;

/// <summary>
/// 시뮬 세션 하나 — 인덱스 + 엔진 + 마지막으로 내보낸 상태.
///
/// 브라우저 WASM 은 단일 스레드라 엔진이 드라이버 스레드를 못 띄운다
/// (<c>Composition.fs</c> 의 <c>hostDrivenLoop</c>). 그래서 <b>호스트가 시계를 민다</b>.
/// 같은 입력과 펌프 시각을 사용해 실행을 재현한다. 펌프 간격은 실행 시각에 영향을 준다.
/// UI 상태 표본(Snapshot/Delta)과 엔진이 발행한 상태 사건 스트림은 별도로 제공한다.
/// </summary>
internal sealed class SimSession : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public int StoreHandle { get; }

    private readonly EventDrivenEngine _engine;
    private readonly ISimulationEngine _sim;
    private readonly Ds2.Runtime.Engine.Core.SimIndex _index;
    private readonly Guid[] _workIds;
    private readonly Guid[] _callIds;
    private readonly Guid[] _apiCallIds;
    private readonly Dictionary<Guid, Status4> _lastWork = [];
    private readonly Dictionary<Guid, Status4> _lastCall = [];
    // Event projection has its own state. UI sampling must never consume it.
    private readonly Dictionary<Guid, Status4> _eventWorkStates = [];
    private readonly Dictionary<Guid, Status4> _eventCallStates = [];

    // Snapshot/Delta remains a UI sampling contract. This separate bounded stream records
    // every Work/Call state change, including shared Reference states and several
    // changes in one pump. Reference projection uses the engine's synchronous
    // transition notification, never interpolation from a later UI snapshot.
    private readonly Queue<StateEvent> _events = new();
    private readonly string _streamId = Guid.NewGuid().ToString("N");
    private int _eventCapacity = 65536;
    private long _sequence;
    private long _epoch;
    private long _captureId = 1;
    private long _captureStartSequence = 1;
    private long _captureStartTimestampMs;
    private long _droppedCount;
    private long _totalDroppedCount;
    private long? _firstDroppedSequence;
    private long? _lastDroppedSequence;
    private long _discardedByCaptureReset;
    private long _drainedCount;
    private string _eventPhase = "execution";
    private bool _disposed;
    private readonly SimulationExecutionOptions _executionOptions;

    private sealed record StateEvent(
        long sequence, long epoch, long timestampMs, string kind, string phase,
        string? entity, string? id, string? previousState, string? newState,
        bool? isSkipped, string? reason);

    private long _clockMs;
    private bool _started;

    private SimSession(int storeHandle, DsStore store, Ds2.Runtime.Engine.Core.SimIndex index, EventDrivenEngine engine,
        SimulationExecutionOptions executionOptions)
    {
        StoreHandle = storeHandle;
        _index = index;
        _engine = engine;
        _sim = engine;
        _executionOptions = executionOptions;
        _workIds = store.Works.Values.Select(w => w.Id).ToArray();
        _callIds = store.Calls.Values.Select(c => c.Id).ToArray();
        _apiCallIds = store.ApiCalls.Keys.OrderBy(id => id).ToArray();
        _sim.WorkStateChanged += OnWorkStateChanged;
        _sim.CallStateChanged += OnCallStateChanged;
        RecordBoundary("open");
        RecordObservations("initial");
    }

    public static SimSession Create(int storeHandle, DsStore store) => Create(storeHandle, store, SimulationExecutionOptions.Legacy);

    public static SimSession Create(int storeHandle, DsStore store, SimulationExecutionOptions options)
    {
        var index = Ds2.Runtime.Engine.Core.SimIndexModule.build(store, 10);
        var engine = new EventDrivenEngine(index, RuntimeMode.Simulation, null, options);
        return new SimSession(storeHandle, store, index, engine, options);
    }

    private sealed record ExecutionOptionsDescription(string version, string initialization, string rearming);

    private ExecutionOptionsDescription DescribeOptions() => new("ds2-sim-options/v1",
        _executionOptions.Initialization == SimulationInitializationPolicy.ModelOnly ? "model-only" : "legacy-auto-homing",
        _executionOptions.Rearming == SimulationRearmingPolicy.ResetEdgesOnly ? "reset-edges-only" : "legacy-device-recovery");

    public string ReadOptions() => JsonSerializer.Serialize(DescribeOptions());

    public bool IsHostDriven => _engine.IsHostDrivenLoop;

    /// <summary>
    /// Promaker 의 시작 절차를 그대로 옮긴 것. <b>순서가 전부다.</b>
    ///   StartWithHomingPhase (내부에서 초기 상태 적용) → Ready 인 TokenSource 전부 StartSourceWork
    /// <c>ForceWorkState(Going)</c> 은 Source 가 아닌 Work 용이고,
    /// <c>Step()</c> 은 STEP UI 전용이다 — 드라이버가 하던 일은 시계 전진이다.
    /// </summary>
    public string Start()
    {
        // Play/resume may be requested repeatedly in one epoch. Initialization and
        // source token seeding belong to its first Start only; Reset opens a new run.
        if (_started)
        {
            _sim.Start();
            return Snapshot();
        }

        RecordBoundary("start");
        _eventPhase = "initialization";
        try
        {
            // StartWithHomingPhase already applies initial states. Applying them here
            // again fabricated a second Ready -> Finish notification at time zero.
            _sim.StartWithHomingPhase();

            foreach (var g in _index.TokenSourceGuids)
            {
                var st = _sim.GetWorkState(g);
                if (st != null && st.Value == Status4.Ready) _sim.StartSourceWork(g);
            }
        }
        finally { _eventPhase = "execution"; }

        _started = true;
        _clockMs = _sim.CurrentTimeMs;
        return Snapshot();
    }

    /// <summary>논리 시간을 밀고 <b>바뀐 것만</b> 돌려준다.</summary>
    public string AdvanceTo(long targetMs)
    {
        if (!_started) throw new InvalidOperationException("Call SimStart before advancing a new or reset simulation session.");
        if (targetMs > _clockMs)
        {
            _sim.AdvanceSimulationTo(targetMs);
            _clockMs = targetMs;
        }
        return Delta();
    }

    public void Reset()
    {
        RecordBoundary("reset-begin");
        _eventPhase = "reset";
        try
        {
            // Preserve notifications emitted while the old run is reset. They belong to
            // the old epoch, with the engine's own timestamps rather than the host target.
            _sim.Reset();
            _started = false;
            _lastWork.Clear();
            _lastCall.Clear();
            _clockMs = 0;
            _epoch++;
            RecordBoundary("reset");
            // ResetState may replace state without a transition notification. Observe it
            // explicitly; never pretend the difference was an engine transition event.
            RecordObservations("reset");
        }
        finally { _eventPhase = "execution"; }
    }

    /// <summary>
    /// Engine event stream, independent of Snapshot/Delta. Reading does not consume it.
    /// Draining returns the same envelope and then removes only the returned records.
    /// Overflow evicts the oldest record and is always reported, including after drains.
    /// </summary>
    public string ReadEvents(bool drain = false)
    {
        var records = _events.ToArray();
        var result = JsonSerializer.Serialize(new
        {
            executionOptions = DescribeOptions(),
            contract = "ds2-sim-events/v1",
            streamId = _streamId,
            epoch = _epoch,
            captureId = _captureId,
            captureStartSequence = _captureStartSequence,
            captureStartTimestampMs = _captureStartTimestampMs,
            clockMs = _sim.CurrentTimeMs,
            capacity = _eventCapacity,
            lastSequence = _sequence,
            firstBufferedSequence = records.Length > 0 ? (long?)records[0].sequence : null,
            lastBufferedSequence = records.Length > 0 ? (long?)records[^1].sequence : null,
            bufferedCount = records.Length,
            remainingCount = drain ? 0 : records.Length,
            drained = drain,
            drainedCount = _drainedCount + (drain ? records.Length : 0),
            overflow = _droppedCount > 0,
            droppedCount = _droppedCount,
            totalDroppedCount = _totalDroppedCount,
            firstDroppedSequence = _firstDroppedSequence,
            lastDroppedSequence = _lastDroppedSequence,
            discardedByCaptureReset = _discardedByCaptureReset,
            events = records,
        }, Json);
        if (drain)
        {
            _events.Clear();
            _drainedCount += records.Length;
        }
        return result;
    }

    /// <summary>
    /// Start a new capture window WITHOUT resetting or starting the engine. Sequence and
    /// run epoch remain monotonic. Explicitly discarded pending records are counted.
    /// Capacity is a record count (1..262144); initial observations also count toward it.
    /// </summary>
    public string ResetEvents(int capacity)
    {
        if (capacity < 1 || capacity > 262144)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Event capacity must be between 1 and 262144.");
        _discardedByCaptureReset += _events.Count;
        _events.Clear();
        _eventCapacity = capacity;
        _captureId++;
        _captureStartSequence = _sequence + 1;
        _captureStartTimestampMs = _sim.CurrentTimeMs;
        _droppedCount = 0;
        _firstDroppedSequence = null;
        _lastDroppedSequence = null;
        RecordBoundary("capture-reset");
        RecordObservations("capture-reset");
        return ReadEvents();
    }

    private void OnWorkStateChanged(object? sender, WorkStateChangedArgs args)
    {
        var group = Ds2.Runtime.Engine.Core.SimIndexModule.referenceGroupOf(_index, args.WorkGuid);
        foreach (var id in group.OrderBy(id => id == args.WorkGuid ? 0 : 1).ThenBy(id => id))
        {
            var actual = _sim.GetWorkState(id);
            if (actual is null || actual.Value != args.NewState)
                throw new InvalidOperationException("Reference Work state disagrees with its transition notification.");
            var previous = _eventWorkStates.TryGetValue(id, out var known) ? known : args.PreviousState;
            if (previous == args.NewState) continue;
            _eventWorkStates[id] = args.NewState;
            RecordEvent("transition", _eventPhase, args.Clock.Ticks / TimeSpan.TicksPerMillisecond,
                "work", "W:" + id, Code(previous), Code(args.NewState),
                reason: id == args.WorkGuid ? null : "reference-state:W:" + args.WorkGuid);
        }
    }

    private void OnCallStateChanged(object? sender, CallStateChangedArgs args)
    {
        var group = Ds2.Runtime.Engine.Core.SimIndexModule.callReferenceGroupOf(_index, args.CallGuid);
        foreach (var id in group.OrderBy(id => id == args.CallGuid ? 0 : 1).ThenBy(id => id))
        {
            var actual = _sim.GetCallState(id);
            if (id == args.CallGuid && (actual is null || actual.Value != args.NewState))
                throw new InvalidOperationException("Reference Call state disagrees with its transition notification.");
            // Legacy homing display forces each Call separately. Project a shared
            // change only once the member actually changed; do not manufacture
            // a future state for another alias awaiting its own notification.
            if (actual is null || actual.Value != args.NewState) continue;
            var previous = _eventCallStates.TryGetValue(id, out var known) ? known : args.PreviousState;
            if (previous == args.NewState) continue;
            _eventCallStates[id] = args.NewState;
            RecordEvent("transition", _eventPhase, args.Clock.Ticks / TimeSpan.TicksPerMillisecond,
                "call", "C:" + id, Code(previous), Code(args.NewState), args.IsSkipped,
                id == args.CallGuid ? null : "reference-state:C:" + args.CallGuid);
        }
    }

    private void RecordBoundary(string reason) =>
        RecordEvent("boundary", "session", _sim.CurrentTimeMs, reason: reason);

    private void RecordObservations(string reason)
    {
        _eventWorkStates.Clear();
        _eventCallStates.Clear();
        var time = _sim.CurrentTimeMs;
        foreach (var id in _workIds)
        {
            var state = _sim.GetWorkState(id);
            if (state != null)
            {
                _eventWorkStates[id] = state.Value;
                RecordEvent("observation", reason, time, "work", "W:" + id, newState: Code(state.Value));
            }
        }
        foreach (var id in _callIds)
        {
            var state = _sim.GetCallState(id);
            if (state != null)
            {
                _eventCallStates[id] = state.Value;
                RecordEvent("observation", reason, time, "call", "C:" + id, newState: Code(state.Value));
            }
        }
    }

    private void RecordEvent(string kind, string phase, long timestampMs,
        string? entity = null, string? id = null, string? previousState = null,
        string? newState = null, bool? isSkipped = null, string? reason = null)
    {
        if (_disposed) return;
        var record = new StateEvent(++_sequence, _epoch, timestampMs, kind, phase,
            entity, id, previousState, newState, isSkipped, reason);
        if (_events.Count == _eventCapacity)
        {
            var lost = _events.Dequeue();
            _droppedCount++;
            _totalDroppedCount++;
            _firstDroppedSequence ??= lost.sequence;
            _lastDroppedSequence = lost.sequence;
        }
        _events.Enqueue(record);
    }

    /// <summary>
    /// Read actual model bits and input-store presence separately. No ValueSpec
    /// default, RxWork fallback, condition evaluation or state mutation occurs.
    /// These are point observations, not a complete value-change event stream.
    /// V1 preserves its unspecified origin contract. V2 distinguishes engine
    /// synthesis from unspecified writes; neither authenticates physical IO.
    /// </summary>
    public string ReadObservations() => ReadObservationsCore(false);

    public string ReadObservationsWithOrigins() => ReadObservationsCore(true);

    private string ReadObservationsCore(bool includeOrigins)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SimSession));
        var state = _sim.State;
        var works = _workIds.Select(id =>
        {
            var found = state.WorkStates.TryGetValue(id, out var status);
            return new
            {
                id = "W:" + id,
                presence = found ? "present" : "missing",
                state = found ? Code(status) : null,
                resultBit = found ? (int?)(status is Status4.Finish or Status4.Homing ? 1 : 0) : null,
                origin = "model-state",
            };
        }).ToArray();
        var inputs = _apiCallIds.Select(id =>
        {
            var present = state.IOValues.TryGetValue(id, out var value);
            return new
            {
                id = "A:" + id,
                presence = present ? "present" : "missing",
                rawValue = present ? value : null,
                valueEpoch = state.IOValueEpoch.TryGetValue(id, out var version) ? (int?)version : null,
                changedAtMs = state.IOValueChangedAt.TryGetValue(id, out var changedAt) ? (double?)changedAt.TotalMilliseconds : null,
                origin = includeOrigins && present
                    && state.IOValueOrigins.TryGetValue(id, out var origin)
                    && origin == InputValueOrigin.EngineSynthesis
                        ? "engine-synthesized" : "runtime-store-unspecified",
            };
        }).ToArray();
        return JsonSerializer.Serialize(new
        {
            contract = includeOrigins ? "ds2-sim-observations/v2" : "ds2-sim-observations/v1",
            streamId = _streamId,
            epoch = _epoch,
            clockMs = state.Clock.TotalMilliseconds,
            executionOptions = DescribeOptions(),
            works,
            inputs,
            scope = "point-observations-not-complete-value-events",
        }, Json);
    }

    public string Snapshot()
    {
        _lastWork.Clear();
        _lastCall.Clear();
        return Delta(full: true);
    }

    private string Delta(bool full = false)
    {
        var works = new List<object>();
        var calls = new List<object>();

        foreach (var id in _workIds)
        {
            var st = _sim.GetWorkState(id);
            if (st == null) continue;
            if (!full && _lastWork.TryGetValue(id, out var prev) && prev == st.Value) continue;
            _lastWork[id] = st.Value;
            works.Add(new { id = "W:" + id, state = Code(st.Value) });
        }

        foreach (var id in _callIds)
        {
            var st = _sim.GetCallState(id);
            if (st == null) continue;
            if (!full && _lastCall.TryGetValue(id, out var prev) && prev == st.Value) continue;
            _lastCall[id] = st.Value;
            calls.Add(new { id = "C:" + id, state = Code(st.Value) });
        }

        return JsonSerializer.Serialize(new
        {
            reset = full,
            clockMs = _clockMs,
            status = _sim.Status.ToString().ToLowerInvariant(),
            works,
            calls,
        }, Json);
    }

    // 노드 id 접두사는 HierarchyBuilder 의 공개 규약(W: / C:)과 같아야 뷰가 찾는다.
    private static string Code(Status4 s) => s switch
    {
        Status4.Going => "going",
        Status4.Finish => "finish",
        Status4.Homing => "homing",
        _ => "ready",
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sim.WorkStateChanged -= OnWorkStateChanged;
        _sim.CallStateChanged -= OnCallStateChanged;
        try { _sim.Stop(); } catch { /* 이미 멈춘 엔진 */ }
        // ISimulationEngine 이 IDisposable 을 명시적으로 구현한다 — 인터페이스로 받아야 부를 수 있다.
        ((IDisposable)_sim).Dispose();
        _events.Clear();
    }
}
