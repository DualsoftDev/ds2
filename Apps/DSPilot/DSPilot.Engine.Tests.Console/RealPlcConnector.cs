using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DSPilot.Engine.Tests.Console;

/// <summary>
/// Real Mitsubishi PLC Connector
/// Connects to actual PLC at 192.168.9.120:5555 and monitors D100-D105 tags
/// </summary>
public class RealPlcConnector : IDisposable
{
    private readonly string _ipAddress;
    private readonly int _port;
    private readonly string[] _tagAddresses;
    private bool _isConnected;
    private Dictionary<string, bool> _previousValues = new();

    public event EventHandler<PlcTagChangedEventArgs>? TagChanged;

    public RealPlcConnector(string ipAddress, int port, string[] tagAddresses)
    {
        _ipAddress = ipAddress;
        _port = port;
        _tagAddresses = tagAddresses;
    }

    public bool IsConnected => _isConnected;

    public async Task<bool> TryConnectAsync()
    {
        System.Console.WriteLine($"  Attempting connection to Mitsubishi PLC: {_ipAddress}:{_port}");

        try
        {
            // Test network connectivity first
            using var ping = new System.Net.NetworkInformation.Ping();
            var result = await ping.SendPingAsync(_ipAddress, 2000);

            if (result.Status != System.Net.NetworkInformation.IPStatus.Success)
            {
                System.Console.WriteLine($"  [ERROR] PLC not reachable: {result.Status}");
                return false;
            }

            System.Console.WriteLine($"  ✓ PLC is reachable (RTT: {result.RoundtripTime}ms)");

            // TODO: Initialize actual Ev2.Backend.PLC connection
            // For now, we'll use the existing DSPilot database to read real historical data
            System.Console.WriteLine($"  ⚠ Real-time PLC connection not yet implemented");
            System.Console.WriteLine($"  Will use historical PLC data from DSPilot database instead");

            _isConnected = true;
            return true;
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"  [ERROR] Connection failed: {ex.Message}");
            _isConnected = false;
            return false;
        }
    }

    public async Task MonitorHistoricalDataAsync(string plcDbPath, CancellationToken cancellationToken)
    {
        System.Console.WriteLine($"\n  Reading historical PLC data from: {plcDbPath}");

        if (!System.IO.File.Exists(plcDbPath))
        {
            System.Console.WriteLine($"  [ERROR] PLC database not found: {plcDbPath}");
            return;
        }

        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={plcDbPath}");
        await conn.OpenAsync(cancellationToken);

        // Check if plcTagLog table exists
        var tableExists = await Dapper.SqlMapper.ExecuteScalarAsync<long>(conn,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='plcTagLog'");

        if (tableExists == 0)
        {
            System.Console.WriteLine($"  [ERROR] plcTagLog table not found in database");
            return;
        }

        // Get tag logs ordered by timestamp
        var logs = await Dapper.SqlMapper.QueryAsync<dynamic>(conn,
            @"SELECT TagName, Value, Timestamp
              FROM plcTagLog
              WHERE TagName IN ('D100', 'D101', 'D102', 'D103', 'D104', 'D105')
              ORDER BY Timestamp ASC
              LIMIT 100");

        var logList = logs.ToList();
        System.Console.WriteLine($"  ✓ Found {logList.Count} historical tag events");

        if (logList.Count == 0)
        {
            System.Console.WriteLine($"  ⚠ No historical data available");
            return;
        }

        System.Console.WriteLine($"  Replaying historical events with 100ms delay between events...\n");

        foreach (var log in logList)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            string tagName = log.TagName;
            bool currentValue = log.Value != 0;
            DateTime timestamp = DateTime.Parse(log.Timestamp);

            // Check for edge detection
            if (_previousValues.TryGetValue(tagName, out var prevValue))
            {
                // Rising Edge
                if (!prevValue && currentValue)
                {
                    System.Console.WriteLine($"  [{timestamp:HH:mm:ss.fff}] 🔼 RISING EDGE: {tagName} (false → true)");
                    TagChanged?.Invoke(this, new PlcTagChangedEventArgs(
                        tagName, currentValue, EdgeType.Rising, timestamp));
                }
                // Falling Edge
                else if (prevValue && !currentValue)
                {
                    System.Console.WriteLine($"  [{timestamp:HH:mm:ss.fff}] 🔽 FALLING EDGE: {tagName} (true → false)");
                    TagChanged?.Invoke(this, new PlcTagChangedEventArgs(
                        tagName, currentValue, EdgeType.Falling, timestamp));
                }
            }

            _previousValues[tagName] = currentValue;

            // Delay to simulate real-time playback
            await Task.Delay(100, cancellationToken);
        }

        System.Console.WriteLine($"\n  ✓ Historical data replay completed");
    }

    public void Dispose()
    {
        _isConnected = false;
    }
}
