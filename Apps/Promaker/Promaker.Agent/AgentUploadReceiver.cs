using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using Microsoft.Extensions.Hosting;
using Promaker.Shared;

namespace Promaker.Agent;

/// <summary>
/// 모델 업로드 수신구 — Promaker(원격, 보통 Windows)가 'Agent에 업로드 ▸ 네트워크' 로 보낸 zip 을 받아
/// 이 머신의 공유 디렉터리에 풀고 active.flag 를 세워 모니터링을 (재)시작시킨다.
///
/// MonitoringSupervisor(파일워처)와 독립으로 항상 listen 하는 이유: Agent 가 idle(active.flag 없음)이면
/// 5051 SignalR Hub(BackendHost)가 꺼져 있어, 그것에 수신을 얹으면 "첫 업로드를 받을 수단이 없는"
/// 닭-달걀이 된다. 그래서 모니터링 on/off 와 무관하게 별도 포트(5050)로 항상 떠 있는다.
/// HttpListener(BCL, 크로스플랫폼) 사용 — 추가 의존성 없음.
///
/// 프로토콜: POST /upload, body=zip. 엔트리는 project.aasx / PlcConnection.json / session.json.
///   - aasx·PlcConnection 은 공유 경로로 그대로 추출.
///   - session.json 은 보낸 쪽(Windows) 경로가 박혀 있으므로, AasxPath·PlcConnectionPath 를 이 머신의
///     로컬 SharedPaths 로 교정해 다시 쓴다(+active.flag) — 안 그러면 Agent 가 Windows 경로를 못 찾는다.
/// </summary>
public sealed class AgentUploadReceiver : BackgroundService
{
    private static readonly ILog Log = LogManager.GetLogger("Promaker.Agent");

    /// <summary>업로드 수신 포트(모니터링 on/off 무관 항상 listen). Promaker AgentUploadClient.Port 와 동일.</summary>
    public const int Port = 5050;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpListener _listener = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _listener.Prefixes.Add($"http://*:{Port}/");
        try
        {
            _listener.Start();
        }
        catch (Exception ex)
        {
            // 포트 충돌/권한 등 — 업로드 수신만 불가, Agent 본체(모니터링)는 계속 동작하도록 조용히 반환.
            Log.Error($"모델 업로드 수신구 시작 실패 (포트 {Port}) — 네트워크 업로드 비활성, 로컬/파일 경로는 정상.", ex);
            return;
        }
        Log.Info($"모델 업로드 수신구 listen: http://*:{Port}/upload");
        stoppingToken.Register(() => { try { _listener.Stop(); } catch { /* 종료 중 */ } });

        while (!stoppingToken.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"업로드 수신 대기 오류: {ex.Message}");
                continue;
            }
            _ = Task.Run(() => HandleAsync(ctx), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            // 헬스 체크 — Promaker 가 전송 전 도달성 확인용.
            if (req.HttpMethod == "GET" && req.Url?.AbsolutePath == "/ping")
            {
                await WriteAsync(ctx, 200, "pong").ConfigureAwait(false);
                return;
            }
            if (req.HttpMethod != "POST" || req.Url?.AbsolutePath != "/upload")
            {
                await WriteAsync(ctx, 404, "not found").ConfigureAwait(false);
                return;
            }

            using var ms = new MemoryStream();
            await req.InputStream.CopyToAsync(ms).ConfigureAwait(false);
            ms.Position = 0;

            Directory.CreateDirectory(SharedPaths.SharedDirectory);
            Directory.CreateDirectory(SharedPaths.AgentDirectory);

            bool sawSession = false;
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                foreach (var entry in zip.Entries)
                {
                    switch (entry.Name)
                    {
                        case "project.aasx":
                            entry.ExtractToFile(SharedPaths.AasxFilePath, overwrite: true);
                            break;
                        case "PlcConnection.json":
                            entry.ExtractToFile(SharedPaths.PlcConnectionFilePath, overwrite: true);
                            break;
                        case "session.json":
                            using (var sr = new StreamReader(entry.Open()))
                            {
                                var json = await sr.ReadToEndAsync().ConfigureAwait(false);
                                var sess = SafeDeserialize(json);
                                // 보낸 쪽(Windows) 경로 → 이 머신 로컬 경로로 교정.
                                sess.AasxPath = SharedPaths.AasxFilePath;
                                sess.PlcConnectionPath = SharedPaths.PlcConnectionFilePath;
                                // session.json + active.flag 동시 기록 → MonitoringSupervisor 파일워처가 감지해 (재)시작.
                                sess.TryWrite();
                            }
                            sawSession = true;
                            break;
                    }
                }
            }

            // session 이 없으면(모델만 보낸 경우) 모델 파일만 갱신하고 활성 신호는 세우지 않는다 —
            // 이미 active 면 aasx 변경을 파일워처가 잡아 재구독, idle 이면 그대로 idle 유지.
            Log.Info(sawSession
                ? "모델 업로드 수신 완료 — 공유 폴더 배치 + active.flag(모니터링 시작)."
                : "모델 업로드 수신 완료 — 모델만 갱신(세션 없음, 활성 상태 불변).");
            await WriteAsync(ctx, 200, sawSession ? "uploaded-active" : "uploaded-model-only").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("업로드 처리 실패", ex);
            try { await WriteAsync(ctx, 500, ex.Message).ConfigureAwait(false); } catch { /* 응답도 실패 */ }
        }
    }

    private static AgentSession SafeDeserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<AgentSession>(json, JsonOpts)
                   ?? AgentSession.ForCurrentDefaults("promaker-remote");
        }
        catch
        {
            return AgentSession.ForCurrentDefaults("promaker-remote");
        }
    }

    private static async Task WriteAsync(HttpListenerContext ctx, int code, string body)
    {
        ctx.Response.StatusCode = code;
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        ctx.Response.Close();
    }
}
