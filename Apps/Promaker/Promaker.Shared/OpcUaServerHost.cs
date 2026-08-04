using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Ds2.Core.Store;
using Ds2.OpcUa.Server.Server;
using log4net;

namespace Promaker.Shared;

/// <summary>OPC UA 서버 구동 결과. UI와 Agent 로그가 함께 사용하는 계약.</summary>
public sealed record OpcUaServerHostResult(
    bool Success,
    string Message,
    string? EndpointUrl = null,
    int AssetCount = 0);

/// <summary>
/// <see cref="EmbeddedUaServer"/>의 프로세스 수명주기를 소유한다.
/// WPF 데모와 Agent 서비스가 같은 구현을 사용하되 각 프로세스는 독립 인스턴스를 가진다.
/// </summary>
public sealed class OpcUaServerHost : IAsyncDisposable
{
    private static readonly ILog Log = LogManager.GetLogger("OpcUaServerHost");

    /// <summary>WPF 데모 호환용 프로세스 전역 인스턴스.</summary>
    public static OpcUaServerHost Instance { get; } = new();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _dataRoot;
    private EmbeddedUaServer? _server;

    public OpcUaServerHost(string? dataRoot = null)
    {
        _dataRoot = string.IsNullOrWhiteSpace(dataRoot) ? DefaultDataRoot : dataRoot;
    }

    /// <summary>WPF 데모용 기본 인증서·상태 저장 루트.</summary>
    public static string DefaultDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Dualsoft", "Promaker", "OpcUa");

    public string DataRoot => _dataRoot;
    public bool IsRunning => _server?.IsRunning ?? false;
    public string? RunningEndpointUrl => _server?.EndpointUrl;
    public EmbeddedUaServer? Server => _server;

    public Task<OpcUaServerHostResult> StartFromSettingsAsync(
        string settingsPath,
        DsStore? store = null,
        CancellationToken ct = default)
        => StartAsync(OpcUaServerSettings.LoadOrDefault(settingsPath), store, ct);

    /// <summary>
    /// Stops the current server and rebuilds its address space from the current settings/store.
    /// </summary>
    public async Task<OpcUaServerHostResult> RestartFromSettingsAsync(
        string settingsPath,
        DsStore? store = null,
        CancellationToken ct = default)
    {
        var stopped = await StopAsync(ct).ConfigureAwait(false);
        if (!stopped.Success)
            return stopped;

        return await StartFromSettingsAsync(settingsPath, store, ct).ConfigureAwait(false);
    }

    public async Task<OpcUaServerHostResult> StartAsync(
        OpcUaServerSettings settings,
        DsStore? store = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.Enabled)
            return new OpcUaServerHostResult(true, "OPC UA 서버 사용 안 함 (설정에서 비활성).");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_server is { IsRunning: true })
                return new OpcUaServerHostResult(true, "이미 실행 중입니다.", _server.EndpointUrl);

            Directory.CreateDirectory(_dataRoot);
            var candidate = new EmbeddedUaServer(
                root: _dataRoot,
                endpointUrl: settings.EndpointUrl,
                applicationName: settings.ApplicationName,
                applicationUri: settings.ApplicationUri,
                allowAnonymous: settings.AllowAnonymous,
                allowUnsecuredEndpoint: settings.AllowUnsecuredEndpoint,
                autoAcceptUntrustedCertificates: settings.AutoAcceptUntrustedCertificates,
                maxSessions: settings.MaxSessions,
                sessionTimeoutMs: settings.SessionTimeoutMs,
                minSamplingIntervalMs: settings.MinSamplingIntervalMs,
                defaultSamplingIntervalMs: settings.DefaultSamplingIntervalMs,
                allowExternalEventInjection: settings.AllowExternalEventInjection);

            int assetCount;
            try
            {
                if (store is null)
                {
                    await candidate.StartAsync().ConfigureAwait(false);
                    assetCount = 0;
                }
                else
                {
                    assetCount = await candidate.StartForStoreAsync(
                            store,
                            exposeKpi: true,
                            exposeLiveTags: true,
                            exposeSimulationData: true)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"OPC UA 서버 기동 실패: {ex.Message}", ex);
                try { await candidate.StopAsync().ConfigureAwait(false); } catch { }
                return new OpcUaServerHostResult(false, $"기동 실패: {ex.Message}");
            }

            _server = candidate;
            var message = store is null
                ? "구동됨 (스토어 미주입 — 빈 서버)."
                : $"구동됨 (Asset {assetCount}개 노출).";
            Log.Info($"OPC UA 서버 기동 완료. endpoint={candidate.EndpointUrl} assets={assetCount}");
            return new OpcUaServerHostResult(true, message, candidate.EndpointUrl, assetCount);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OpcUaServerHostResult> StopAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = _server;
            // Prevent a bridge from attaching to an instance that is being stopped.
            _server = null;
            if (current is null || !current.IsRunning)
            {
                return new OpcUaServerHostResult(true, "이미 정지 상태.");
            }

            try
            {
                await current.StopAsync().ConfigureAwait(false);
                Log.Info("OPC UA 서버 정지.");
                return new OpcUaServerHostResult(true, "정지됨.");
            }
            catch (Exception ex)
            {
                Log.Error($"OPC UA 서버 정지 실패: {ex.Message}", ex);
                return new OpcUaServerHostResult(false, $"정지 실패: {ex.Message}");
            }
            finally
            {
                _server = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
