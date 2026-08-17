using Ds2.Backend.Plc;
using Ds2.OpcUa.Server.Server;
using log4net;
using Opc.Ua;

namespace Promaker.Shared;

public interface IAidTelemetrySink
{
    bool Publish(AidSouthboundSignalDescriptor signal, object value, DateTime sourceTimestamp, uint statusCode);
    void SetQuality(IEnumerable<string> signalIds, uint statusCode, DateTime sourceTimestamp);
    bool PublishEvent(AidSouthboundEventDescriptor descriptor, DateTime sourceTimestamp, string payloadJson);
}

public sealed class EmbeddedUaAidTelemetrySink(EmbeddedUaServer server) : IAidTelemetrySink
{
    public bool Publish(AidSouthboundSignalDescriptor signal, object value, DateTime sourceTimestamp, uint statusCode) =>
        server.WriteAidSignal(signal.SignalId, value, sourceTimestamp, statusCode);

    public void SetQuality(IEnumerable<string> signalIds, uint statusCode, DateTime sourceTimestamp) =>
        server.SetAidSignalQuality(signalIds, statusCode, sourceTimestamp);

    public bool PublishEvent(AidSouthboundEventDescriptor descriptor, DateTime sourceTimestamp, string payloadJson) =>
        server.RaiseAidEvent(descriptor.SignalId, descriptor.EventTypeSemanticId, sourceTimestamp, payloadJson);
}

internal interface IAidEndpointAdapter : IAsyncDisposable
{
    string Name { get; }
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync();
}

internal abstract class AidEndpointAdapter(
    AidSouthboundEndpointDescriptor endpoint,
    IAidTelemetrySink sink,
    IAidSecretResolver secretResolver) : IAidEndpointAdapter
{
    protected static readonly ILog Log = LogManager.GetLogger("AidSouthbound");
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private DateTimeOffset _credentialsExpireAt = DateTimeOffset.MinValue;

    protected AidSouthboundEndpointDescriptor Endpoint { get; } = endpoint;
    protected IAidTelemetrySink Sink { get; } = sink;
    protected IAidSecretResolver SecretResolver { get; } = secretResolver;
    protected AidCredentials Credentials { get; private set; } = AidCredentials.Anonymous;
    public string Name => Endpoint.Name;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_worker is not null) return;
        await RefreshCredentialsAsync(cancellationToken, force: true).ConfigureAwait(false);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        SetAllQuality(StatusCodes.BadWaitingForInitialData);
        _worker = Task.Run(() => RunProtectedAsync(_cts.Token), CancellationToken.None);
    }

    private async Task RunProtectedAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ExecuteAsync(cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested) break;
                failures++;
                SetAllQuality(StatusCodes.BadInternalError);
                Log.Error($"AID adapter worker exited unexpectedly: endpoint={Endpoint.Name} " +
                          $"attempts={failures}; restarting.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                SetAllQuality(StatusCodes.BadInternalError);
                Log.Error($"AID adapter worker terminated unexpectedly: endpoint={Endpoint.Name} " +
                          $"attempts={failures}; restarting.", ex);
            }

            try
            {
                await Task.Delay(RetryDelay(failures), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    protected abstract Task ExecuteAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Refreshes Vault/local credentials at most once per minute. Connection-oriented adapters
    /// call this before every reconnect; HTTP polling calls it from its live loop so rotated
    /// secrets take effect without restarting the Agent.
    /// </summary>
    protected async Task RefreshCredentialsAsync(CancellationToken cancellationToken, bool force = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && now < _credentialsExpireAt) return;
        var resolved = await SecretResolver.ResolveAsync(Endpoint.AuthReferenceVault, cancellationToken)
            .ConfigureAwait(false);
        AidCredentialRules.ValidateCommon(resolved, Endpoint.AuthReferenceVault);
        ValidateCredentials(resolved);
        Credentials = resolved;
        _credentialsExpireAt = now.AddMinutes(1);
    }

    protected virtual void ValidateCredentials(AidCredentials credentials) { }

    public async Task StopAsync()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        var worker = Interlocked.Exchange(ref _worker, null);
        if (cts is null) return;
        cts.Cancel();
        try { if (worker is not null) await worker.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally { cts.Dispose(); }
        SetAllQuality(StatusCodes.BadShutdown);
    }

    protected void SetAllQuality(uint statusCode) =>
        Sink.SetQuality(
            Endpoint.Signals.Select(item => item.SignalId)
                .Concat(Endpoint.Events.Select(item => item.SignalId)),
            statusCode,
            DateTime.UtcNow);

    protected void SetQuality(AidSouthboundSignalDescriptor signal, uint statusCode) =>
        Sink.SetQuality([signal.SignalId], statusCode, DateTime.UtcNow);

    protected static TimeSpan RetryDelay(int failures) =>
        TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(failures, 6))));

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}

internal static class AidCredentialRules
{
    private const int MaxFieldLength = 16 * 1024;
    private const int MaxHeaders = 32;

    internal static void ValidateCommon(AidCredentials credentials, string reference)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        CheckLength(credentials.Username, "username");
        CheckLength(credentials.Password, "password");
        CheckLength(credentials.BearerToken, "bearerToken");
        CheckLength(credentials.ClientId, "clientId");
        if (!string.IsNullOrEmpty(credentials.Password) && string.IsNullOrWhiteSpace(credentials.Username))
            throw new InvalidOperationException("AID credential password requires username.");
        if (credentials.Headers is not null)
        {
            if (credentials.Headers.Count > MaxHeaders)
                throw new InvalidOperationException($"AID credentials contain more than {MaxHeaders} headers.");
            foreach (var (name, value) in credentials.Headers)
            {
                if (!IsHeaderName(name))
                    throw new InvalidOperationException($"AID credential header name '{name}' is invalid.");
                CheckLength(value, $"header '{name}'");
                if (value.Contains('\r') || value.Contains('\n'))
                    throw new InvalidOperationException($"AID credential header '{name}' contains a newline.");
            }
        }

        if (!string.IsNullOrWhiteSpace(reference)
            && string.IsNullOrWhiteSpace(credentials.Username)
            && string.IsNullOrWhiteSpace(credentials.BearerToken)
            && string.IsNullOrWhiteSpace(credentials.ClientId)
            && (credentials.Headers is null || credentials.Headers.Count == 0))
            throw new InvalidOperationException("AID authReferenceVault resolved to no usable credential fields.");
    }

    internal static bool HasHeaders(AidCredentials credentials) =>
        credentials.Headers is { Count: > 0 };

    private static void CheckLength(string? value, string name)
    {
        if (value?.Length > MaxFieldLength)
            throw new InvalidOperationException($"AID credential {name} exceeds {MaxFieldLength} characters.");
    }

    private static bool IsHeaderName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256) return false;
        const string separators = "()<>@,;:\\\"/[]?={} \t";
        return value.All(character => character > 31 && character < 127 && !separators.Contains(character));
    }
}

/// <summary>
/// Owns every standard AID southbound adapter for one active Agent model.
/// XGT remains on the shared PLC gateway; this runtime owns OPC UA, Modbus,
/// MQTT, and HTTP bindings.
/// </summary>
public sealed class AidSouthboundRuntime : IAsyncDisposable
{
    private static readonly ILog Log = LogManager.GetLogger("AidSouthboundRuntime");
    private readonly List<IAidEndpointAdapter> _adapters;
    private readonly IAidSecretResolver _secretResolver;
    private bool _started;
    private int _disposeState;

    public AidSouthboundRuntime(
        AidSouthboundConfigResult plan,
        EmbeddedUaServer uaServer,
        string dataRoot,
        IAidSecretResolver? secretResolver = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(uaServer);
        if (!plan.Success) throw new ArgumentException(string.Join(" / ", plan.Errors), nameof(plan));
        Directory.CreateDirectory(dataRoot);
        _secretResolver = secretResolver ?? new AidSecretResolver();
        var sink = new EmbeddedUaAidTelemetrySink(uaServer);
        _adapters = plan.Endpoints.Select(endpoint =>
        {
            if (endpoint.Protocol.Equals(AidSouthboundProtocol.OpcUa))
                return (IAidEndpointAdapter)new OpcUaAidEndpointAdapter(endpoint, sink, _secretResolver, dataRoot);
            if (endpoint.Protocol.Equals(AidSouthboundProtocol.Modbus))
                return new ModbusAidEndpointAdapter(endpoint, sink, _secretResolver);
            if (endpoint.Protocol.Equals(AidSouthboundProtocol.Mqtt))
                return new MqttAidEndpointAdapter(endpoint, sink, _secretResolver);
            if (endpoint.Protocol.Equals(AidSouthboundProtocol.Http))
                return new HttpAidEndpointAdapter(endpoint, sink, _secretResolver);
            throw new ArgumentOutOfRangeException(nameof(endpoint.Protocol));
        }).ToList();
    }

    public int EndpointCount => _adapters.Count;

    /// <summary>네트워크 연결 전에 프로토콜별 주소/필터 문법을 모두 검증한다.</summary>
    public static bool TryValidatePlan(AidSouthboundConfigResult plan, out string[] errors)
    {
        var found = new List<string>();
        foreach (var endpoint in plan.Endpoints)
        {
            try
            {
                if (endpoint.Protocol.Equals(AidSouthboundProtocol.OpcUa))
                    OpcUaAidEndpointAdapter.ValidateConfiguration(endpoint);
                else if (endpoint.Protocol.Equals(AidSouthboundProtocol.Modbus))
                    ModbusAidEndpointAdapter.ValidateConfiguration(endpoint);
                else if (endpoint.Protocol.Equals(AidSouthboundProtocol.Mqtt))
                    MqttAidEndpointAdapter.ValidateConfiguration(endpoint);
                else if (endpoint.Protocol.Equals(AidSouthboundProtocol.Http))
                    HttpAidEndpointAdapter.ValidateConfiguration(endpoint);
                else
                    throw new InvalidOperationException($"Unsupported AID protocol '{endpoint.Protocol}'.");
            }
            catch (Exception ex)
            {
                found.Add($"{endpoint.Name}: {ex.Message}");
            }
        }
        errors = found.ToArray();
        return errors.Length == 0;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        if (_started) return;
        var started = new List<IAidEndpointAdapter>();
        try
        {
            foreach (var adapter in _adapters)
            {
                // Each adapter owns a linked CTS for its worker. Pass the caller token directly so
                // cancellation remains registered after this startup method returns.
                await adapter.StartAsync(cancellationToken).ConfigureAwait(false);
                started.Add(adapter);
            }
            _started = true;
            Log.Info($"AID southbound active: endpoints={_adapters.Count}");
        }
        catch
        {
            foreach (var adapter in started.AsEnumerable().Reverse())
                await adapter.StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (!_started) return;
        _started = false;
        foreach (var adapter in _adapters.AsEnumerable().Reverse())
        {
            try { await adapter.StopAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn($"AID adapter stop failed: {adapter.Name}", ex); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0) return;
        await StopAsync().ConfigureAwait(false);
        foreach (var adapter in _adapters) await adapter.DisposeAsync().ConfigureAwait(false);
        if (_secretResolver is IDisposable disposable) disposable.Dispose();
    }
}
