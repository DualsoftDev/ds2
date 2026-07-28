using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Ds2.Core.Store;
using Ds2.OpcUa.Server.Server;
using log4net;
using Promaker.Shared;

namespace Promaker.Services;

/// <summary>OPC UA 서버 구동 결과. UI 알림/로그 용.</summary>
public sealed record OpcUaServerHostResult(bool Success, string Message, string? EndpointUrl = null, int AssetCount = 0);

/// <summary>
/// Ds2.OpcUa.Server 의 <see cref="EmbeddedUaServer"/> 를 Promaker 프로세스 안에서 소유·관리.
/// <see cref="OpcUaServerSettings.Enabled"/> 가 true 일 때만 실제 서버를 기동한다.
///
/// 동시성 · 재진입:
///   - Start/Stop 은 세마포로 직렬화 — WPF 이벤트에서 여러 번 트리거되어도 안전.
///   - StartAsync 는 이미 기동 중이면 즉시 성공 반환 (idempotent).
///   - StopAsync 는 실행 중이 아니면 no-op.
///
/// 인증서 · 데이터 루트: <c>%AppData%\Dualsoft\Promaker\OpcUa\</c> 아래. 첫 기동 시 self-signed 자동 발급.
/// </summary>
public sealed class OpcUaServerHost : IAsyncDisposable
{
    private static readonly ILog Log = LogManager.GetLogger("OpcUaServerHost");

    /// <summary>프로세스 전역 싱글턴 — Promaker 는 UA 서버 인스턴스를 하나만 유지.</summary>
    public static OpcUaServerHost Instance { get; } = new();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private EmbeddedUaServer? _server;

    private OpcUaServerHost() { }

    /// <summary><c>%AppData%\Dualsoft\Promaker\OpcUa</c>. Certs · nodeset-state 저장 루트.</summary>
    public static string DefaultDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Dualsoft", "Promaker", "OpcUa");

    /// <summary>true 면 UA 서버가 현재 구동 중.</summary>
    public bool IsRunning => _server?.IsRunning ?? false;

    /// <summary>구동 중이라면 서버가 바인딩한 endpoint. 아니면 null.</summary>
    public string? RunningEndpointUrl => _server?.EndpointUrl;

    /// <summary>
    /// 설정 파일 경로에서 <see cref="OpcUaServerSettings"/> 를 읽어와 필요시 서버 기동.
    /// <paramref name="store"/> 가 주어지면 프로젝트 활성 System 을 Asset 으로,
    /// KPI/Work/Call/IO 를 하위 Variable 로 브라우징 트리에 노출한다.
    /// <c>Enabled=false</c> 이면 아무 것도 하지 않고 성공 반환.
    /// </summary>
    public async Task<OpcUaServerHostResult> StartFromSettingsAsync(
        string settingsPath, DsStore? store = null, CancellationToken ct = default)
    {
        var settings = OpcUaServerSettings.LoadOrDefault(settingsPath);
        return await StartAsync(settings, store, ct).ConfigureAwait(false);
    }

    /// <summary><see cref="OpcUaServerSettings"/> 로 서버 기동. <c>Enabled=false</c> 면 skip.
    /// <paramref name="store"/> null 이면 빈 서버 (Server 표준 노드만), 값이 있으면 Asset · Variable 트리 로드.</summary>
    public async Task<OpcUaServerHostResult> StartAsync(
        OpcUaServerSettings settings, DsStore? store = null, CancellationToken ct = default)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));

        if (!settings.Enabled)
        {
            Log.Info("OPC UA 서버 미사용 — StartAsync skip.");
            return new OpcUaServerHostResult(true, "OPC UA 서버 사용 안 함 (설정에서 비활성).");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_server is { IsRunning: true })
            {
                Log.Info($"OPC UA 서버 이미 구동 중 → skip. endpoint={_server.EndpointUrl}");
                return new OpcUaServerHostResult(true, "이미 실행 중입니다.", _server.EndpointUrl);
            }

            Directory.CreateDirectory(DefaultDataRoot);

            var srv = new EmbeddedUaServer(
                root: DefaultDataRoot,
                endpointUrl: settings.EndpointUrl,
                applicationName: settings.ApplicationName,
                applicationUri: settings.ApplicationUri,
                allowAnonymous: settings.AllowAnonymous,
                maxSessions: settings.MaxSessions,
                sessionTimeoutMs: settings.SessionTimeoutMs,
                minSamplingIntervalMs: settings.MinSamplingIntervalMs,
                defaultSamplingIntervalMs: settings.DefaultSamplingIntervalMs);

            int assetCount = 0;
            try
            {
                if (store is null)
                    await srv.StartAsync().ConfigureAwait(false);
                else
                    assetCount = await srv.StartForStoreAsync(
                        store, exposeKpi: true, exposeLiveTags: true, exposeSimulationData: true)
                        .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error($"OPC UA 서버 기동 실패: {ex.Message}", ex);
                try { await srv.StopAsync().ConfigureAwait(false); } catch { /* best-effort */ }
                return new OpcUaServerHostResult(false, $"기동 실패: {ex.Message}");
            }

            _server = srv;
            var msg = store is null
                ? "구동됨 (스토어 미주입 — 빈 서버)."
                : $"구동됨 (Asset {assetCount}개 노출).";
            Log.Info($"OPC UA 서버 기동 완료. endpoint={srv.EndpointUrl} assets={assetCount}");
            return new OpcUaServerHostResult(true, msg, srv.EndpointUrl, assetCount);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>실행 중 서버의 <see cref="EmbeddedUaServer"/> 참조 (진단/WriteRuntimeIo 배선 용).</summary>
    public EmbeddedUaServer? Server => _server;

    /// <summary>서버 정지. 실행 중이 아니면 no-op.</summary>
    public async Task<OpcUaServerHostResult> StopAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var srv = _server;
            if (srv is null || !srv.IsRunning)
            {
                return new OpcUaServerHostResult(true, "이미 정지 상태.");
            }

            try
            {
                await srv.StopAsync().ConfigureAwait(false);
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
