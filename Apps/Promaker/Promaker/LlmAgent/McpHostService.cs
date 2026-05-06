using System;
using System.Linq;
using System.Net;
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
        Log.Info($"McpHostService 시작 — url={ServerUrl}, nonce length={HandshakeNonce.Length}");
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
