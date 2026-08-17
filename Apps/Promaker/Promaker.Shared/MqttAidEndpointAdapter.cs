using System.Text;
using System.Buffers;
using Ds2.Backend.Plc;
using MQTTnet;
using MQTTnet.Protocol;
using Opc.Ua;

namespace Promaker.Shared;

internal sealed class MqttAidEndpointAdapter(
    AidSouthboundEndpointDescriptor endpoint,
    IAidTelemetrySink sink,
    IAidSecretResolver secretResolver) : AidEndpointAdapter(endpoint, sink, secretResolver)
{
    internal static void ValidateConfiguration(AidSouthboundEndpointDescriptor endpoint)
    {
        foreach (var signal in endpoint.Signals)
        {
            var filter = signal.Href;
            if (string.IsNullOrWhiteSpace(filter) || filter.Contains('\0')
                || Encoding.UTF8.GetByteCount(filter) > ushort.MaxValue)
                throw new FormatException($"Invalid MQTT topic filter for signalId '{signal.SignalId}'.");
            var levels = filter.Split('/');
            for (var index = 0; index < levels.Length; index++)
            {
                var level = levels[index];
                if (level.Contains('#') && (level != "#" || index != levels.Length - 1))
                    throw new FormatException($"MQTT '#' must occupy the final level for signalId '{signal.SignalId}'.");
                if (level.Contains('+') && level != "+")
                    throw new FormatException($"MQTT '+' must occupy an entire level for signalId '{signal.SignalId}'.");
            }
        }
    }

    protected override void ValidateCredentials(AidCredentials credentials)
    {
        if (!string.IsNullOrWhiteSpace(credentials.BearerToken) || AidCredentialRules.HasHeaders(credentials))
            throw new InvalidOperationException(
                "AID MQTT supports username/password and clientId credentials only; bearerToken and headers are not supported.");
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            IMqttClient? client = null;
            try
            {
                await RefreshCredentialsAsync(cancellationToken).ConfigureAwait(false);
                var uri = new Uri(Endpoint.BaseAddress, UriKind.Absolute);
                var factory = new MqttClientFactory();
                client = factory.CreateMqttClient();
                var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                client.DisconnectedAsync += _ =>
                {
                    disconnected.TrySetResult();
                    return Task.CompletedTask;
                };
                client.ApplicationMessageReceivedAsync += args =>
                {
                    HandleMessage(args.ApplicationMessage.Topic, args.ApplicationMessage.Payload);
                    return Task.CompletedTask;
                };

                var optionsBuilder = new MqttClientOptionsBuilder()
                    .WithClientId(Credentials.ClientId ?? $"ds2-agent-{Guid.NewGuid():N}")
                    .WithCleanSession()
                    .WithTimeout(TimeSpan.FromSeconds(15));
                if (!string.IsNullOrWhiteSpace(Credentials.Username))
                    optionsBuilder.WithCredentials(Credentials.Username, Credentials.Password ?? "");
                var useTls = uri.Scheme == "mqtts" ||
                             Endpoint.Security.Contains("tls", StringComparison.OrdinalIgnoreCase);
                var port = uri.IsDefaultPort ? (useTls ? 8883 : 1883) : uri.Port;
                var connectHost = useTls
                    ? uri.Host
                    : (await PrivateNetworkConnection.ResolveAddressAsync(uri.Host, cancellationToken)
                        .ConfigureAwait(false)).ToString();
                optionsBuilder.WithTcpServer(connectHost, port);
                if (useTls) optionsBuilder.WithTlsOptions(builder => builder.UseTls());

                await client.ConnectAsync(optionsBuilder.Build(), cancellationToken).ConfigureAwait(false);
                foreach (var signal in Endpoint.Signals)
                {
                    var subscribe = new MqttClientSubscribeOptionsBuilder()
                        .WithTopicFilter(filter => filter
                            .WithTopic(signal.Href)
                            .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)signal.Qos))
                        .Build();
                    await client.SubscribeAsync(subscribe, cancellationToken).ConfigureAwait(false);
                }
                failures = 0;
                await disconnected.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                throw new IOException("MQTT client disconnected.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                failures++;
                SetAllQuality(StatusCodes.BadNoCommunication);
                Log.Warn($"AID MQTT connection failed: endpoint={Endpoint.Name}: {ex.Message}");
                await Task.Delay(RetryDelay(failures), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (client is not null)
                {
                    try { if (client.IsConnected) await client.DisconnectAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false); }
                    catch { }
                    client.Dispose();
                }
            }
        }
    }

    private void HandleMessage(string topic, ReadOnlySequence<byte> payload)
    {
        if (payload.Length > 4 * 1024 * 1024)
        {
            Log.Warn($"AID MQTT payload rejected (>4 MiB): endpoint={Endpoint.Name} topic={topic}");
            foreach (var signal in Endpoint.Signals.Where(signal => TopicMatches(signal.Href, topic)))
                SetQuality(signal, StatusCodes.BadEncodingLimitsExceeded);
            return;
        }
        var text = Encoding.UTF8.GetString(payload.ToArray());
        foreach (var signal in Endpoint.Signals)
        {
            if (!TopicMatches(signal.Href, topic)) continue;
            try
            {
                object raw = string.IsNullOrWhiteSpace(signal.PayloadPath)
                    ? text
                    : AidValueCodec.ExtractJson(text, signal.PayloadPath);
                var typed = AidValueCodec.ConvertScalar(raw, signal.ValueType);
                Sink.Publish(signal, typed, DateTime.UtcNow, StatusCodes.Good);
            }
            catch (InvalidDataException ex)
            {
                SetQuality(signal, StatusCodes.BadEncodingLimitsExceeded);
                Log.Warn($"AID MQTT value exceeds UA transport limits: signalId={signal.SignalId}: {ex.Message}");
            }
            catch (Exception ex)
            {
                SetQuality(signal, StatusCodes.BadTypeMismatch);
                Log.Warn($"AID MQTT payload conversion failed: signalId={signal.SignalId}: {ex.Message}");
            }
        }
    }

    internal static bool TopicMatches(string filter, string topic)
    {
        var filterParts = filter.Split('/');
        var topicParts = topic.Split('/');
        for (var i = 0; i < filterParts.Length; i++)
        {
            if (filterParts[i] == "#") return i == filterParts.Length - 1;
            if (i >= topicParts.Length) return false;
            if (filterParts[i] != "+" && !string.Equals(filterParts[i], topicParts[i], StringComparison.Ordinal))
                return false;
        }
        return topicParts.Length == filterParts.Length;
    }
}
