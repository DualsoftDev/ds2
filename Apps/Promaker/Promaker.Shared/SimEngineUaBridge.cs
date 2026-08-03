using System;
using System.Collections.Generic;
using System.Threading;
using Ds2.Core;
using Ds2.OpcUa.Server.Server;
using Ds2.Runtime.Engine;
using Ds2.Runtime.Model;
using log4net;

namespace Promaker.Shared;

/// <summary>
/// <see cref="ISimulationEngine"/> 상태를 <see cref="EmbeddedUaServer"/> Variable로 반영한다.
/// Work/Call은 이벤트 기반, IOValues는 엔진 계약상 변경 이벤트가 없어 주기적으로 차분 반영한다.
/// </summary>
public sealed class SimEngineUaBridge : IDisposable
{
    private static readonly ILog Log = LogManager.GetLogger("SimEngineUaBridge");
    private readonly ISimulationEngine _engine;
    private readonly EmbeddedUaServer _uaServer;
    private readonly Timer _ioPollTimer;
    private readonly object _writeGate = new();
    private volatile bool _disposed;
    private Microsoft.FSharp.Collections.FSharpMap<Guid, string>? _lastIoSnapshot;

    public SimEngineUaBridge(ISimulationEngine engine, EmbeddedUaServer uaServer, int pollIntervalMs = 200)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _uaServer = uaServer ?? throw new ArgumentNullException(nameof(uaServer));
        var interval = Math.Max(50, pollIntervalMs);

        _engine.WorkStateChanged += OnWorkStateChanged;
        _engine.CallStateChanged += OnCallStateChanged;
        PushInitialSnapshot();
        _ioPollTimer = new Timer(PollIoTick, null, interval, interval);
    }

    private void PushInitialSnapshot()
    {
        try
        {
            var now = DateTime.UtcNow;
            foreach (var workGuid in _uaServer.WorkStateGuids)
            {
                var state = _engine.GetWorkState(workGuid);
                var text = Microsoft.FSharp.Core.FSharpOption<Status4>.get_IsSome(state)
                    ? state.Value.ToString()
                    : Status4.Ready.ToString();
                _uaServer.WriteWorkState(workGuid, text, now);
            }

            foreach (var callGuid in _uaServer.CallStateGuids)
            {
                var state = _engine.GetCallState(callGuid);
                var text = Microsoft.FSharp.Core.FSharpOption<Status4>.get_IsSome(state)
                    ? state.Value.ToString()
                    : Status4.Ready.ToString();
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
        lock (_writeGate)
        {
            if (_disposed) return;
            try { _uaServer.WriteWorkState(e.WorkGuid, e.NewState.ToString(), DateTime.UtcNow); }
            catch (Exception ex) { Log.Warn($"WorkState push 예외 · work={e.WorkGuid}: {ex.Message}"); }
        }
    }

    private void OnCallStateChanged(object? sender, CallStateChangedArgs e)
    {
        lock (_writeGate)
        {
            if (_disposed) return;
            try { _uaServer.WriteCallState(e.CallGuid, e.NewState.ToString(), DateTime.UtcNow); }
            catch (Exception ex) { Log.Warn($"CallState push 예외 · call={e.CallGuid}: {ex.Message}"); }
        }
    }

    private void PollIoTick(object? _)
    {
        lock (_writeGate)
        {
            if (_disposed) return;
            try
            {
                var current = _engine.State.IOValues;
                var diff = new Dictionary<Guid, string>();
                var previous = _lastIoSnapshot;
                if (previous is null)
                {
                    foreach (var pair in current) diff[pair.Key] = pair.Value;
                }
                else
                {
                    foreach (var pair in current)
                    {
                        var oldValue = Microsoft.FSharp.Collections.MapModule.TryFind(pair.Key, previous);
                        if (!Microsoft.FSharp.Core.FSharpOption<string>.get_IsSome(oldValue) || oldValue.Value != pair.Value)
                            diff[pair.Key] = pair.Value;
                    }
                }

                _lastIoSnapshot = current;
                if (diff.Count > 0) _uaServer.WriteRuntimeIo(diff);
            }
            catch (Exception ex)
            {
                Log.Warn($"IO 폴링 tick 예외: {ex.Message}");
            }
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
        // A callback that already entered before Dispose must finish before the final quality transition.
        lock (_writeGate)
        {
            try { _uaServer.SetRuntimeQuality(Opc.Ua.StatusCodes.BadOutOfService, DateTime.UtcNow); }
            catch (Exception ex) { Log.Warn($"Final OPC UA stop-quality transition failed: {ex.Message}"); }
        }
    }
}
