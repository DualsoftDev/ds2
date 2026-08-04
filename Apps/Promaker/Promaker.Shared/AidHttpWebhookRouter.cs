using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ds2.Backend.Plc;
using Ds2.OpcUa.Server.Server;
using log4net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Promaker.Shared;

/// <summary>Maps non-polled AID HTTP interactions into the Agent Kestrel host.</summary>
public sealed class AidHttpWebhookRouter : IAsyncDisposable
{
    private static readonly ILog Log = LogManager.GetLogger("AidHttpWebhookRouter");
    private const int MaxRequestBytes = 4 * 1024 * 1024;
    private readonly IAidSecretResolver _secrets;
    private readonly bool _ownsSecrets;
    private readonly Route[] _routes;
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, AidCredentials Value)> _credentialCache = new();
    private readonly ConcurrentDictionary<string, RequestWindow> _requestWindows = new(StringComparer.Ordinal);
    private readonly int _requestsPerMinute = ReadIntEnvironment("DS2_AID_WEBHOOK_REQUESTS_PER_MINUTE", 600, 10, 100_000);
    private long _rateLimitChecks;
    private IAidTelemetrySink? _sink;

    public AidHttpWebhookRouter(AidSouthboundConfigResult plan, IAidSecretResolver? secretResolver = null)
    {
        _secrets = secretResolver ?? new AidSecretResolver();
        _ownsSecrets = secretResolver is null;
        _routes = plan.Endpoints
            .Where(endpoint => endpoint.Protocol.Equals(AidSouthboundProtocol.Http))
            .SelectMany(endpoint => endpoint.Signals
                .Where(signal => !signal.PollIntervalMs.HasValue)
                .Select(signal => new Route(endpoint, signal, AbsolutePath(endpoint.BaseAddress, signal.Href))))
            .ToArray();
    }

    public int RouteCount => _routes.Length;

    public void Attach(EmbeddedUaServer server) => _sink = new EmbeddedUaAidTelemetrySink(server);
    public void Detach() => _sink = null;

    public void Map(WebApplication app)
    {
        foreach (var route in _routes)
        {
            app.MapMethods(route.Path, [route.Signal.Operation], context => HandleAsync(route, context));
            Log.Info($"AID HTTP webhook mapped: {route.Signal.Operation} {route.Path} -> {route.Signal.SignalId}");
        }
    }

    private async Task HandleAsync(Route route, HttpContext context)
    {
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (_sink is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        if (!AllowRequest(context))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }
        if (context.Request.ContentLength > MaxRequestBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }
        try
        {
            var credentials = await CredentialsAsync(route.Endpoint, context.RequestAborted).ConfigureAwait(false);
            if (!Authorized(context.Request, credentials))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            var payload = await ReadLimitedAsync(context.Request.Body, context.RequestAborted).ConfigureAwait(false);
            object raw = string.IsNullOrWhiteSpace(route.Signal.PayloadPath)
                ? payload
                : AidValueCodec.ExtractJson(payload, route.Signal.PayloadPath);
            var typed = AidValueCodec.ConvertScalar(raw, route.Signal.ValueType);
            if (!_sink.Publish(route.Signal, typed, DateTime.UtcNow, Opc.Ua.StatusCodes.Good))
                throw new InvalidOperationException("AID signal node was not found in the active UA model.");
            context.Response.StatusCode = StatusCodes.Status202Accepted;
        }
        catch (JsonException ex)
        {
            Log.Warn($"AID webhook JSON rejected: signalId={route.Signal.SignalId}: {ex.Message}");
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
        }
        catch (FormatException ex)
        {
            Log.Warn($"AID webhook value rejected: signalId={route.Signal.SignalId}: {ex.Message}");
            context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        }
        catch (InvalidDataException)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        }
        catch (Exception ex)
        {
            Log.Error($"AID webhook failed: signalId={route.Signal.SignalId}", ex);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        }
    }

    private async Task<AidCredentials> CredentialsAsync(
        AidSouthboundEndpointDescriptor endpoint, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(endpoint.AuthReferenceVault)) return AidCredentials.Anonymous;
        if (_credentialCache.TryGetValue(endpoint.Name, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
            return cached.Value;
        var value = await _secrets.ResolveAsync(endpoint.AuthReferenceVault, cancellationToken).ConfigureAwait(false);
        AidCredentialRules.ValidateCommon(value, endpoint.AuthReferenceVault);
        if (string.IsNullOrWhiteSpace(value.BearerToken)
            && string.IsNullOrWhiteSpace(value.Username)
            && !AidCredentialRules.HasHeaders(value))
            throw new InvalidOperationException("AID webhook requires bearerToken, username/password, or a custom header credential.");
        _credentialCache[endpoint.Name] = (DateTimeOffset.UtcNow.AddMinutes(1), value);
        return value;
    }

    private static bool Authorized(HttpRequest request, AidCredentials credentials)
    {
        var hasCredential = false;
        if (!string.IsNullOrWhiteSpace(credentials.BearerToken))
        {
            hasCredential = true;
            if (!FixedEquals(request.Headers.Authorization.ToString(), "Bearer " + credentials.BearerToken)) return false;
        }
        else if (!string.IsNullOrWhiteSpace(credentials.Username))
        {
            hasCredential = true;
            var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{credentials.Username}:{credentials.Password ?? ""}"));
            if (!FixedEquals(request.Headers.Authorization.ToString(), "Basic " + raw)) return false;
        }
        if (credentials.Headers is not null)
            foreach (var (name, expected) in credentials.Headers)
            {
                hasCredential = true;
                if (!request.Headers.TryGetValue(name, out var actual) || !FixedEquals(actual.ToString(), expected))
                    return false;
            }
        return hasCredential;
    }

    private bool AllowRequest(HttpContext context)
    {
        var key = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var now = DateTimeOffset.UtcNow;
        if ((Interlocked.Increment(ref _rateLimitChecks) & 0xff) == 0)
            RemoveExpiredRequestWindows(now);
        var window = _requestWindows.GetOrAdd(key, _ => new RequestWindow(now));
        lock (window)
        {
            if (now - window.Start >= TimeSpan.FromMinutes(1))
            {
                window.Start = now;
                window.Count = 0;
            }
            window.Count++;
            return window.Count <= _requestsPerMinute;
        }
    }

    private void RemoveExpiredRequestWindows(DateTimeOffset now)
    {
        foreach (var pair in _requestWindows)
        {
            var expired = false;
            lock (pair.Value)
                expired = now - pair.Value.Start >= TimeSpan.FromMinutes(2);
            if (expired)
                ((ICollection<KeyValuePair<string, RequestWindow>>)_requestWindows).Remove(pair);
        }
    }

    private static bool FixedEquals(string actual, string expected)
    {
        var left = SHA256.HashData(Encoding.UTF8.GetBytes(actual));
        var right = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static async Task<string> ReadLimitedAsync(Stream source, CancellationToken cancellationToken)
    {
        var chunk = new byte[16 * 1024];
        using var buffer = new MemoryStream();
        while (true)
        {
            var read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > MaxRequestBytes)
                throw new InvalidDataException("AID webhook request exceeds 4 MiB.");
            buffer.Write(chunk, 0, read);
        }
        try
        {
            return new UTF8Encoding(false, true).GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
        }
        catch (DecoderFallbackException ex)
        {
            throw new JsonException("AID webhook request is not valid UTF-8.", ex);
        }
    }

    private static string AbsolutePath(string baseAddress, string href) =>
        new Uri(new Uri(baseAddress, UriKind.Absolute), href).AbsolutePath;

    private static int ReadIntEnvironment(string name, int fallback, int minimum, int maximum) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var parsed)
            ? Math.Clamp(parsed, minimum, maximum)
            : fallback;

    public ValueTask DisposeAsync()
    {
        Detach();
        if (_ownsSecrets && _secrets is IDisposable disposable) disposable.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed record Route(
        AidSouthboundEndpointDescriptor Endpoint,
        AidSouthboundSignalDescriptor Signal,
        string Path);

    private sealed class RequestWindow(DateTimeOffset start)
    {
        public DateTimeOffset Start { get; set; } = start;
        public int Count { get; set; }
    }
}
