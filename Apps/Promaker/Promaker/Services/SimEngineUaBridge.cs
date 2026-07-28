using System;
using System.Collections.Generic;
using System.Threading;
using Ds2.Core;
using Ds2.OpcUa.Server.Server;
using Ds2.Runtime.Engine;
using Ds2.Runtime.Model;
using log4net;

namespace Promaker.Services;

/// <summary>
/// SimEngine 상태 → EmbeddedUaServer 로 값 push 하는 브릿지.
///
/// 없으면 UA 클라이언트에서 read 시 <c>BadWaitingForInitialData</c> 로 응답 —
/// LoadStore 는 노드를 등록만 하고 값 채우기는 이 브릿지가 담당한다.
///
/// 세 경로:
///   1) Work state — engine.WorkStateChanged 이벤트 → WriteWorkState
///   2) Call state — engine.CallStateChanged 이벤트 → WriteCallState
///   3) Runtime IO — engine.State.IOValues 를 주기 폴링 → WriteRuntimeIo
///
/// Sim engine 은 상태 전이가 아닌 IOValue 변화 이벤트가 없어서 polling 이 필요.
/// 200ms 주기면 인간 감지 한계보다 빠르고 CPU 부담도 미미.
/// </summary>
public sealed class SimEngineUaBridge : IDisposable
{
    private static readonly ILog Log = LogManager.GetLogger("SimEngineUaBridge");

    private readonly ISimulationEngine _engine;
    private readonly EmbeddedUaServer _uaServer;
    private readonly Timer _ioPollTimer;
    private readonly int _pollIntervalMs;
    private volatile bool _disposed;

    // IOValues 폴링 시 이전 스냅샷 대비 변화만 push 하기 위한 캐시.
    // 초기값 null → 첫 tick 에서 전량 push (BadWaitingForInitialData 해제).
    private Microsoft.FSharp.Collections.FSharpMap<Guid, string>? _lastIoSnapshot;

    public SimEngineUaBridge(ISimulationEngine engine, EmbeddedUaServer uaServer, int pollIntervalMs = 200)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _uaServer = uaServer ?? throw new ArgumentNullException(nameof(uaServer));
        _pollIntervalMs = Math.Max(50, pollIntervalMs);

        _engine.WorkStateChanged += OnWorkStateChanged;
        _engine.CallStateChanged += OnCallStateChanged;

        // 초기 스냅샷 — 등록된 Work/Call 노드를 현재 상태로 채워 BadWaitingForInitialData 해제.
        PushInitialSnapshot();

        _ioPollTimer = new Timer(PollIoTick, null, _pollIntervalMs, _pollIntervalMs);
    }

    private void PushInitialSnapshot()
    {
        try
        {
            var now = DateTime.UtcNow;
            foreach (var workGuid in _uaServer.WorkStateGuids)
            {
                var s = _engine.GetWorkState(workGuid);
                var text = Microsoft.FSharp.Core.FSharpOption<Status4>.get_IsSome(s)
                    ? s.Value.ToString() : Status4.Ready.ToString();
                _uaServer.WriteWorkState(workGuid, text, now);
            }
            foreach (var callGuid in _uaServer.CallStateGuids)
            {
                var s = _engine.GetCallState(callGuid);
                var text = Microsoft.FSharp.Core.FSharpOption<Status4>.get_IsSome(s)
                    ? s.Value.ToString() : Status4.Ready.ToString();
                _uaServer.WriteCallState(callGuid, text, now);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"초기 스냅샷 push 예외: {ex.Message}");
        }
    }

    private void OnWorkStateChanged(object? sender, WorkStateChangedArgs e)
    {
        if (_disposed) return;
        try { _uaServer.WriteWorkState(e.WorkGuid, e.NewState.ToString(), DateTime.UtcNow); }
        catch (Exception ex) { Log.Warn($"WorkState push 예외 · work={e.WorkGuid}: {ex.Message}"); }
    }

    private void OnCallStateChanged(object? sender, CallStateChangedArgs e)
    {
        if (_disposed) return;
        try { _uaServer.WriteCallState(e.CallGuid, e.NewState.ToString(), DateTime.UtcNow); }
        catch (Exception ex) { Log.Warn($"CallState push 예외 · call={e.CallGuid}: {ex.Message}"); }
    }

    private void PollIoTick(object? _)
    {
        if (_disposed) return;
        try
        {
            var current = _engine.State.IOValues;
            var diff = new Dictionary<Guid, string>();
            // 최초 tick 이거나 캐시가 비어있으면 전체 push (BadWaitingForInitialData 해제 목적).
            var previous = _lastIoSnapshot;
            if (previous is null)
            {
                foreach (var kv in current)
                    diff[kv.Key] = kv.Value;
            }
            else
            {
                foreach (var kv in current)
                {
                    var pv = Microsoft.FSharp.Collections.MapModule.TryFind(kv.Key, previous);
                    if (!Microsoft.FSharp.Core.FSharpOption<string>.get_IsSome(pv) || pv.Value != kv.Value)
                        diff[kv.Key] = kv.Value;
                }
            }
            _lastIoSnapshot = current;

            if (diff.Count > 0)
                _uaServer.WriteRuntimeIo(diff);
        }
        catch (Exception ex)
        {
            Log.Warn($"IO 폴링 tick 예외: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _ioPollTimer.Dispose(); } catch { }
        try
        {
            _engine.WorkStateChanged -= OnWorkStateChanged;
            _engine.CallStateChanged -= OnCallStateChanged;
        }
        catch { }
    }
}
