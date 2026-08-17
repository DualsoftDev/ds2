using System.Net.Http.Headers;
using System.Net;
using System.Text;
using Ds2.Backend.Plc;
using Opc.Ua;

namespace Promaker.Shared;

internal sealed class HttpAidEndpointAdapter(
    AidSouthboundEndpointDescriptor endpoint,
    IAidTelemetrySink sink,
    IAidSecretResolver secretResolver) : AidEndpointAdapter(endpoint, sink, secretResolver)
{
    private const int MaxResponseBytes = 4 * 1024 * 1024;

    internal static void ValidateConfiguration(AidSouthboundEndpointDescriptor endpoint)
    {
        foreach (var signal in endpoint.Signals)
        {
            if (signal.Operation is not ("GET" or "POST" or "PUT" or "DELETE"))
                throw new FormatException($"Unsupported HTTP method '{signal.Operation}' for signalId '{signal.SignalId}'.");
            if (!string.IsNullOrWhiteSpace(signal.ContentType)
                && !MediaTypeHeaderValue.TryParse(signal.ContentType, out _))
                throw new FormatException($"Invalid HTTP contentType '{signal.ContentType}' for signalId '{signal.SignalId}'.");
        }
    }

    protected override void ValidateCredentials(AidCredentials credentials)
    {
        if (!string.IsNullOrWhiteSpace(credentials.ClientId))
            throw new InvalidOperationException("AID HTTP does not use the clientId credential field.");
        if (credentials.Headers is not null)
            foreach (var name in credentials.Headers.Keys)
                if (ForbiddenCredentialHeader(name))
                    throw new InvalidOperationException($"AID HTTP credential header '{name}' is not allowed.");
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        var polledSignals = Endpoint.Signals.Where(item => item.PollIntervalMs.HasValue).ToArray();
        var due = polledSignals.ToDictionary(item => item, _ => DateTime.MinValue);
        var failures = polledSignals.ToDictionary(item => item, _ => 0);
        var credentialFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RefreshCredentialsAsync(cancellationToken).ConfigureAwait(false);
                credentialFailures = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                credentialFailures++;
                SetAllQuality(StatusCodes.BadIdentityTokenRejected);
                if (credentialFailures == 1 || credentialFailures % 10 == 0)
                    Log.Warn($"AID HTTP credential refresh failed: endpoint={Endpoint.Name} " +
                             $"attempts={credentialFailures}: {ex.Message}");
                await Task.Delay(RetryDelay(credentialFailures), cancellationToken).ConfigureAwait(false);
                continue;
            }
            var now = DateTime.UtcNow;
            foreach (var signal in polledSignals)
            {
                if (due[signal] > now) continue;
                due[signal] = now.AddMilliseconds(Interval(signal));
                try
                {
                    using var request = CreateRequest(signal);
                    using var response = await client.SendAsync(
                        request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    var payload = await ReadLimitedAsync(response.Content, cancellationToken).ConfigureAwait(false);
                    object raw = string.IsNullOrWhiteSpace(signal.PayloadPath)
                        ? payload
                        : AidValueCodec.ExtractJson(payload, signal.PayloadPath);
                    var typed = AidValueCodec.ConvertScalar(raw, signal.ValueType);
                    Sink.Publish(signal, typed, DateTime.UtcNow, StatusCodes.Good);
                    failures[signal] = 0;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    failures[signal]++;
                    due[signal] = DateTime.UtcNow.Add(RetryDelay(failures[signal]));
                    SetQuality(signal, StatusCodes.BadCommunicationError);
                    if (failures[signal] == 1 || failures[signal] % 10 == 0)
                        Log.Warn($"AID HTTP poll failed: endpoint={Endpoint.Name} signalId={signal.SignalId} " +
                                 $"attempts={failures[signal]}: {ex.Message}");
                }
            }
            var next = due.Count == 0 ? DateTime.UtcNow.AddSeconds(1) : due.Values.Min();
            var delay = next - DateTime.UtcNow;
            await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(10), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private HttpClient CreateClient()
    {
        var baseAddress = new Uri(Endpoint.BaseAddress, UriKind.Absolute);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
        };
        if (baseAddress.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            handler.ConnectCallback = (context, cancellationToken) =>
                PrivateNetworkConnection.ConnectTcpAsync(
                    context.DnsEndPoint.Host,
                    context.DnsEndPoint.Port,
                    cancellationToken);
        }
        return new HttpClient(handler)
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    private static int Interval(AidSouthboundSignalDescriptor signal) =>
        signal.PollIntervalMs.HasValue ? signal.PollIntervalMs.Value :
        signal.SamplingIntervalMs.HasValue ? signal.SamplingIntervalMs.Value : 1000;

    private HttpRequestMessage CreateRequest(AidSouthboundSignalDescriptor signal)
    {
        var method = new HttpMethod(signal.Operation);
        var request = new HttpRequestMessage(method, signal.Href);
        if (method == HttpMethod.Post || method == HttpMethod.Put)
            request.Content = new StringContent("", Encoding.UTF8,
                string.IsNullOrWhiteSpace(signal.ContentType) ? "application/json" : signal.ContentType);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
            string.IsNullOrWhiteSpace(signal.ContentType) ? "application/json" : signal.ContentType));
        if (!string.IsNullOrWhiteSpace(Credentials.BearerToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Credentials.BearerToken);
        else if (!string.IsNullOrWhiteSpace(Credentials.Username))
        {
            var bytes = Encoding.UTF8.GetBytes($"{Credentials.Username}:{Credentials.Password ?? ""}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(bytes));
        }
        if (Credentials.Headers is not null)
            foreach (var (name, value) in Credentials.Headers)
                if (!request.Headers.TryAddWithoutValidation(name, value))
                    throw new InvalidOperationException($"AID HTTP credential header '{name}' is invalid.");
        return request;
    }

    private static bool ForbiddenCredentialHeader(string name) =>
        name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Host", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase);

    private static async Task<string> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaxResponseBytes)
            throw new InvalidDataException("AID HTTP response exceeds 4 MiB.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > MaxResponseBytes)
                throw new InvalidDataException("AID HTTP response exceeds 4 MiB.");
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }
}
