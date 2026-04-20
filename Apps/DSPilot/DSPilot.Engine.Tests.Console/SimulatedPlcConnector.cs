using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DSPilot.Engine.Tests.Console;

/// <summary>
/// Simulated PLC Connector for testing state transitions
/// Generates realistic PLC events matching Mitsubishi D100-D105 tags
/// </summary>
public class SimulatedPlcConnector : IDisposable
{
    private readonly string[] _tagAddresses;
    private bool _isConnected;
    private readonly Random _random = new();

    public event EventHandler<PlcTagChangedEventArgs>? TagChanged;

    public SimulatedPlcConnector(string ipAddress, int port, string[] tagAddresses)
    {
        _tagAddresses = tagAddresses;
    }

    public bool IsConnected => _isConnected;

    public async Task ConnectAsync()
    {
        System.Console.WriteLine($"  Simulated PLC connection established");
        System.Console.WriteLine($"  Monitoring tags: {string.Join(", ", _tagAddresses)}");
        _isConnected = true;
        await Task.CompletedTask;
    }

    public async Task MonitorTagsAsync(CancellationToken cancellationToken)
    {
        System.Console.WriteLine("\n  Starting simulated PLC monitoring...");
        System.Console.WriteLine("  Will generate realistic Work1 cycle: D100 (In) → D101 (Out)\n");

        await Task.Delay(2000, cancellationToken);

        // Simulate Work1 cycle
        await SimulateWorkCycle("D100", "D101", "Work1", cancellationToken);

        await Task.Delay(3000, cancellationToken);

        // Simulate Work2 cycle
        await SimulateWorkCycle("D102", "D103", "Work2", cancellationToken);

        await Task.Delay(2000, cancellationToken);

        // Simulate Work3 cycle
        await SimulateWorkCycle("D104", "D105", "Work3", cancellationToken);

        // Continue monitoring
        System.Console.WriteLine("\n  Simulation completed. Monitoring for additional changes...");

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(5000, cancellationToken);
        }
    }

    private async Task SimulateWorkCycle(string inTag, string outTag, string workName, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return;

        try
        {
            // Simulate InTag Rising Edge (Work Start)
            System.Console.WriteLine($"  [{DateTime.Now:HH:mm:ss.fff}] 🔼 SIMULATED RISING EDGE: {inTag} (false → true) - {workName} Starting");
            TagChanged?.Invoke(this, new PlcTagChangedEventArgs(
                inTag, true, EdgeType.Rising, DateTime.Now));

            // Simulate work duration (500-2000ms)
            var workDuration = _random.Next(500, 2000);
            await Task.Delay(workDuration, cancellationToken);

            // Simulate OutTag Rising Edge (Work Complete)
            System.Console.WriteLine($"  [{DateTime.Now:HH:mm:ss.fff}] 🔼 SIMULATED RISING EDGE: {outTag} (false → true) - {workName} Completed");
            TagChanged?.Invoke(this, new PlcTagChangedEventArgs(
                outTag, true, EdgeType.Rising, DateTime.Now));

            // Simulate tags returning to false after a brief period
            await Task.Delay(200, cancellationToken);

            System.Console.WriteLine($"  [{DateTime.Now:HH:mm:ss.fff}] 🔽 SIMULATED FALLING EDGE: {inTag} (true → false)");
            TagChanged?.Invoke(this, new PlcTagChangedEventArgs(
                inTag, false, EdgeType.Falling, DateTime.Now));

            await Task.Delay(100, cancellationToken);

            System.Console.WriteLine($"  [{DateTime.Now:HH:mm:ss.fff}] 🔽 SIMULATED FALLING EDGE: {outTag} (true → false)");
            TagChanged?.Invoke(this, new PlcTagChangedEventArgs(
                outTag, false, EdgeType.Falling, DateTime.Now));
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
    }

    public void Dispose()
    {
        _isConnected = false;
    }
}

public enum EdgeType
{
    Rising,
    Falling
}

public class PlcTagChangedEventArgs : EventArgs
{
    public string TagName { get; }
    public bool Value { get; }
    public EdgeType EdgeType { get; }
    public DateTime Timestamp { get; }

    public PlcTagChangedEventArgs(string tagName, bool value, EdgeType edgeType, DateTime timestamp)
    {
        TagName = tagName;
        Value = value;
        EdgeType = edgeType;
        Timestamp = timestamp;
    }
}
