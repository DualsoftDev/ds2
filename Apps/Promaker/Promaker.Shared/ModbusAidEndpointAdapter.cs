using System.Globalization;
using System.Net.Sockets;
using Ds2.Backend.Plc;
using NModbus;
using Opc.Ua;

namespace Promaker.Shared;

internal sealed class ModbusAidEndpointAdapter(
    AidSouthboundEndpointDescriptor endpoint,
    IAidTelemetrySink sink,
    IAidSecretResolver secretResolver) : AidEndpointAdapter(endpoint, sink, secretResolver)
{
    internal static void ValidateConfiguration(AidSouthboundEndpointDescriptor endpoint)
    {
        foreach (var signal in endpoint.Signals) _ = ParseAddress(signal);
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var uri = new Uri(Endpoint.BaseAddress, UriKind.Absolute);
                using var client = new TcpClient();
                var remoteAddress = await PrivateNetworkConnection.ResolveAddressAsync(uri.Host, cancellationToken)
                    .ConfigureAwait(false);
                await client.ConnectAsync(remoteAddress, uri.IsDefaultPort ? 502 : uri.Port, cancellationToken)
                    .ConfigureAwait(false);
                var factory = new ModbusFactory();
                using var master = factory.CreateMaster(client);
                master.Transport.ReadTimeout = 5000;
                master.Transport.WriteTimeout = 5000;
                failures = 0;
                var due = Endpoint.Signals.ToDictionary(item => item, _ => DateTime.MinValue);
                while (client.Connected && !cancellationToken.IsCancellationRequested)
                {
                    var now = DateTime.UtcNow;
                    foreach (var signal in Endpoint.Signals)
                    {
                        if (due[signal] > now) continue;
                        due[signal] = now.AddMilliseconds(Interval(signal));
                        try
                        {
                            var address = ParseAddress(signal);
                            object value;
                            if (signal.Operation == "readCoils" || signal.Operation == "readDiscreteInputs")
                            {
                                var values = signal.Operation == "readCoils"
                                    ? await master.ReadCoilsAsync(UnitId, address.Start, 1).WaitAsync(cancellationToken)
                                    : await master.ReadInputsAsync(UnitId, address.Start, 1).WaitAsync(cancellationToken);
                                value = AidValueCodec.ConvertScalar(values[0], signal.ValueType);
                            }
                            else
                            {
                                var registers = signal.Operation == "readHoldingRegisters"
                                    ? await master.ReadHoldingRegistersAsync(UnitId, address.Start, address.Quantity).WaitAsync(cancellationToken)
                                    : await master.ReadInputRegistersAsync(UnitId, address.Start, address.Quantity).WaitAsync(cancellationToken);
                                value = AidValueCodec.DecodeModbusRegisters(
                                    registers, signal.ValueType, signal.MostSignificantWord, signal.Scale, signal.Offset);
                            }
                            Sink.Publish(signal, value, DateTime.UtcNow, StatusCodes.Good);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            SetQuality(signal, StatusCodes.BadCommunicationError);
                            throw new IOException($"signalId={signal.SignalId}: {ex.Message}", ex);
                        }
                    }
                    var next = due.Count == 0 ? DateTime.UtcNow.AddSeconds(1) : due.Values.Min();
                    var delay = next - DateTime.UtcNow;
                    await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(10), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                failures++;
                SetAllQuality(StatusCodes.BadNoCommunication);
                Log.Warn($"AID Modbus connection failed: endpoint={Endpoint.Name}: {ex.Message}");
                await Task.Delay(RetryDelay(failures), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private byte UnitId => Endpoint.UnitId.HasValue ? Endpoint.UnitId.Value : (byte)1;

    private static int Interval(AidSouthboundSignalDescriptor signal) =>
        signal.SamplingIntervalMs.HasValue ? Math.Max(10, signal.SamplingIntervalMs.Value) : 1000;

    internal static (ushort Start, ushort Quantity) ParseAddress(AidSouthboundSignalDescriptor signal)
    {
        var parts = signal.Href.Split('?', 2);
        var addressText = parts[0].Trim().TrimStart('/');
        if (!uint.TryParse(addressText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var logical))
            throw new FormatException($"Invalid Modbus address '{signal.Href}'.");
        uint zeroBased = signal.Operation switch
        {
            "readHoldingRegisters" when logical >= 40001 => logical - 40001,
            "readInputRegisters" when logical >= 30001 => logical - 30001,
            "readDiscreteInputs" when logical >= 10001 => logical - 10001,
            "readCoils" when logical >= 1 => logical - 1,
            _ => logical
        };
        if (zeroBased > ushort.MaxValue) throw new FormatException("Modbus address exceeds 65535.");
        var quantity = AidValueCodec.RequiredRegisters(signal.ValueType);
        if (parts.Length == 2)
        {
            foreach (var pair in parts[1].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var item = pair.Split('=', 2);
                if (item.Length == 2 && item[0].Equals("quantity", StringComparison.OrdinalIgnoreCase) &&
                    ushort.TryParse(item[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                    quantity = parsed;
            }
        }
        if (quantity == 0 || quantity > 125) throw new FormatException("Modbus quantity must be 1..125.");
        return ((ushort)zeroBased, quantity);
    }
}
