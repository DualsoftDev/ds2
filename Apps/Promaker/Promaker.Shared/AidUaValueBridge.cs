using System;
using System.Collections.Generic;
using Ds2.Backend.Common;
using Ds2.Backend.Plc;
using Ds2.OpcUa.Server.Server;
using Opc.Ua;
using log4net;

namespace Promaker.Shared;

/// <summary>
/// Agent로 들어온 주소 기반 PLC batch를 AID의 signalId 기반 OPC UA Variable에 반영한다.
/// 직접 PLC scan과 Pi5 delegated scan 모두 동일한 runtime batch 경계를 사용한다.
/// </summary>
public sealed class AidUaValueBridge
{
    private static readonly ILog Log = LogManager.GetLogger("AidUaValueBridge");
    private readonly EmbeddedUaServer _uaServer;
    private readonly object _writeGate = new();
    private readonly Dictionary<string, List<AidXgtSignalDescriptor>> _byAddress =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string[]> _signalIdsByConnection;
    private readonly Dictionary<string, Ds2.Backend.Common.PlcConnectionStatus> _connectionStates =
        new(StringComparer.OrdinalIgnoreCase);

    public AidUaValueBridge(EmbeddedUaServer uaServer, IEnumerable<AidXgtSignalDescriptor> signals)
    {
        _uaServer = uaServer ?? throw new ArgumentNullException(nameof(uaServer));
        ArgumentNullException.ThrowIfNull(signals);

        var allSignals = signals.ToArray();
        foreach (var signal in allSignals)
        {
            if (!_byAddress.TryGetValue(signal.Address, out var bucket))
                _byAddress[signal.Address] = bucket = [];
            bucket.Add(signal);
        }
        _signalIdsByConnection = allSignals
            .GroupBy(s => s.ConnectionName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(s => s.SignalId).Distinct().ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }

    public int AddressCount => _byAddress.Count;

    public void Observe(TagWrite[]? items)
    {
        if (items is null) return;
        lock (_writeGate)
        {
            foreach (var item in items)
            {
                if (item is null || !_byAddress.TryGetValue(item.Address, out var signals)) continue;
                var sourceTs = item.WallClockMs > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(item.WallClockMs).UtcDateTime
                    : DateTime.UtcNow;

                foreach (var signal in signals)
                {
                    if (_connectionStates.TryGetValue(signal.ConnectionName, out var state) && !state.IsConnected)
                        continue;
                    try
                    {
                        // 중앙 NodeManager가 선언 BuiltInType 기준으로 변환한다.
                        _uaServer.WriteAidSignal(signal.SignalId, item.Value, sourceTs, StatusCodes.Good);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"AID UA 값 변환 실패 · address={item.Address} signalId={signal.SignalId} " +
                                 $"type={signal.ValueType} value={item.Value}: {ex.Message}");
                    }
                }
            }
        }
    }

    public void ObserveConnection(Ds2.Backend.Common.PlcConnectionStatus status)
    {
        lock (_writeGate)
        {
            _connectionStates[status.Name] = status;
            if (!_signalIdsByConnection.TryGetValue(status.Name, out var signalIds)) return;
            var code = status.IsConnected
                ? StatusCodes.UncertainLastUsableValue
                : StatusCodes.BadNoCommunication;
            _uaServer.SetAidSignalQuality(signalIds, code, status.AtUtc.ToUniversalTime());
        }
    }
}
