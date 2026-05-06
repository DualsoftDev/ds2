using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using log4net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Promaker.LlmAgent;

/// <summary>
/// Promaker in-process Kestrel + ModelContextProtocol.AspNetCore HTTP transport.
///
/// 결정 4 (c) HTTP MCP transport 채택의 server 측 구현.
/// 결정 5.0: loopback (127.0.0.1) bind + OS ephemeral port + handshake nonce 헤더 검증.
///
/// Phase 1b-c 골격 — 실제 mutation/read tool 등록은 phase 1c 부터. 현재 PingTool 1개 (검증용).
/// </summary>
public sealed class McpHostService : IAsyncDisposable
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(McpHostService));
    private const string NonceHeader = "X-Promaker-Nonce";

    private WebApplication? _app;

    /// <summary>handshake 검증용 short-lived secret (start 시점 32-byte hex 생성).</summary>
    public string HandshakeNonce { get; private set; } = "";

    /// <summary>Kestrel 이 실제 listen 한 URL (ephemeral port). Start 후에만 유효.</summary>
    public string ServerUrl { get; private set; } = "";

    /// <summary>turn-scoped state holder. Tool method 의 인자로 type 만 적으면 SDK 가
    /// IServiceProviderIsService.IsService(type) 로 자동 검출하여 schema 제외 + DI 주입 (Pass D).</summary>
    public LlmTurnContextProvider TurnProvider { get; } = new();

    /// <summary>Kestrel start. 첫 호출 시 host 띄우고 ServerUrl / HandshakeNonce 확정.</summary>
    public async Task StartAsync()
    {
        if (_app != null) return;

        HandshakeNonce = GenerateNonce();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();   // log4net 와 충돌 회피, ASP.NET Core 자체 로그 disable

        // loopback only + ephemeral port. Kestrel 은 ListenLocalhost(0) 의 동적 port 를 거부 →
        // IPAddress.Loopback (127.0.0.1) + port 0 명시 사용.
        builder.WebHost.ConfigureKestrel(opts =>
        {
            opts.Listen(IPAddress.Loopback, 0);
        });

        // SDK (ModelContextProtocol.AspNetCore 1.2.0) 의 AIFunctionMcpServerTool binder 가
        // IServiceProviderIsService.IsService(type) 로 등록 type 자동 검출 → tool method 인자에서
        // 자동 schema 제외 + DI 주입 (attribute 불필요. Pass D — 이전 [FromKeyedServices(null)] 우회 제거).
        builder.Services.AddSingleton(TurnProvider);

        builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly();   // 같은 assembly 의 [McpServerToolType] 자동 등록

        var app = builder.Build();

        // 결정 5.0: handshake nonce 검증 미들웨어 — MapMcp 보다 먼저 등록
        app.Use(async (context, next) =>
        {
            if (!context.Request.Headers.TryGetValue(NonceHeader, out var v) || v.ToString() != HandshakeNonce)
            {
                Log.Warn($"MCP handshake 거부 (path={context.Request.Path}, ip={context.Connection.RemoteIpAddress})");
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next();
        });

        app.MapMcp();

        await app.StartAsync();

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        ServerUrl = addresses?.Addresses.FirstOrDefault() ?? "";
        if (string.IsNullOrEmpty(ServerUrl))
            throw new InvalidOperationException("Kestrel 시작 후 listening URL 확인 실패.");

        _app = app;

        // Kestrel listen socket bind 가 끝나도 middleware pipeline + nonce 검증 + MapMcp routing 이
        // 실제 request 를 처리하는 시점은 약간 뒤일 수 있음. codex 0.125 는 MCP connect 실패 시
        // silent fallback (tool 비활성화 + 사용자에겐 일반 응답) 으로 진행되어 사용자가 race 를 알아채기
        // 어려움 (Hot-fix-4 시나리오의 정합성 강화). HTTP self-probe 로 routing pipeline 까지 통과 확인.
        await WaitReadyAsync(TimeSpan.FromSeconds(2));

        Log.Info($"McpHostService 시작 — url={ServerUrl}, nonce length={HandshakeNonce.Length}");
    }

    /// <summary>
    /// MCP HTTP endpoint 가 실제 응답할 때까지 retry. Kestrel start 직후에는 socket bind 만 보장되고
    /// pipeline 이 첫 request 를 처리할 준비는 약간 뒤. nonce header 를 동봉하여 자체 미들웨어 통과까지 검증.
    /// 어떤 status code 든 응답이 오면 ready (MCP transport 는 GET 에 405 등 줄 수 있음 — 그 자체가 routing OK 의 신호).
    /// timeout 초과 시 InvalidOperationException — InitializeAsync 의 catch 가 사용자에게 노출.
    /// </summary>
    private async Task WaitReadyAsync(TimeSpan timeout)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
        http.DefaultRequestHeaders.Add(NonceHeader, HandshakeNonce);

        var deadline = DateTime.UtcNow + timeout;
        Exception? lastEx = null;
        var attempts = 0;
        while (DateTime.UtcNow < deadline)
        {
            attempts++;
            try
            {
                using var resp = await http.GetAsync(ServerUrl).ConfigureAwait(false);
                // attempts > 1 = 실제 race window 가 존재한다는 단서 (Kestrel listen 이후 routing pipeline 미준비).
                // 일정 기간 attempts > 1 이 한 번도 안 나오면 WaitReadyAsync 자체 제거 후보 (review 항목 A).
                if (attempts > 1)
                    Log.Info($"MCP HTTP ready probe 통과 (race 발생) — status={(int)resp.StatusCode}, attempts={attempts}");
                else
                    Log.Debug($"MCP HTTP ready probe 통과 — status={(int)resp.StatusCode}, attempts={attempts}");
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // HttpRequestException = connection refused / DNS / TLS 등. TaskCanceledException = HttpClient.Timeout (500ms) 만료.
                // 그 외 (OOM 등) 는 흡수하지 않고 즉시 throw — silent failure 회피.
                lastEx = ex;
                await Task.Delay(50).ConfigureAwait(false);
            }
        }
        throw new InvalidOperationException(
            $"MCP HTTP endpoint readiness check {timeout.TotalMilliseconds:F0}ms 안에 응답 없음 (attempts={attempts})",
            lastEx);
    }

    public async Task StopAsync()
    {
        if (_app == null) return;
        try
        {
            await _app.StopAsync(TimeSpan.FromSeconds(5));
            await _app.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("McpHostService StopAsync 실패", ex);
        }
        finally
        {
            _app = null;
            Log.Info("McpHostService 중지");
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_app == null) return ValueTask.CompletedTask;
        return new ValueTask(StopAsync());
    }

    private static string GenerateNonce()
    {
        var bytes = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }
}
