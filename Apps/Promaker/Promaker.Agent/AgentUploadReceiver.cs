using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Ds2.Aasx;
using Ds2.Core.Store;
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
    private readonly SemaphoreSlim _requestSlots = new(4, 4);
    private readonly ConcurrentDictionary<string, RequestWindow> _requestWindows = new(StringComparer.Ordinal);
    private readonly int _requestsPerMinute = ReadIntEnvironment(
        "DS2_AGENT_TRANSFER_REQUESTS_PER_MINUTE", 600, 10, 100_000);
    private readonly int _requestTimeoutSeconds = ReadIntEnvironment(
        "DS2_AGENT_TRANSFER_REQUEST_TIMEOUT_SECONDS", 600, 30, 3600);
    private long _rateLimitChecks;
    private readonly int _maxUploadBytes = ReadIntEnvironment(
        "DS2_AGENT_TRANSFER_MAX_UPLOAD_BYTES",
        AgentTransferSecurityOptions.DefaultMaxUploadBytes,
        1024 * 1024,
        1024 * 1024 * 1024);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var prefix = $"http://*:{Port}/";
        _listener.Prefixes.Add(prefix);
        try
        {
            _listener.Start();
        }
        catch (Exception ex)
        {
            Log.Error($"모델 업로드 수신구 시작 실패 (포트 {Port}) — 네트워크 업로드 비활성, 로컬/파일 경로는 정상.", ex);
            throw;
        }
        Log.Info($"모델 업로드 수신구 listen: {prefix}upload maxUpload={_maxUploadBytes}");
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
            if (!AllowRequest(ctx))
            {
                await WriteAsync(ctx, 429, "too many requests").ConfigureAwait(false);
                continue;
            }
            if (!_requestSlots.Wait(0))
            {
                await WriteAsync(ctx, 429, "too many requests").ConfigureAwait(false);
                continue;
            }
            _ = Task.Run(async () =>
            {
                using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                requestCts.CancelAfter(TimeSpan.FromSeconds(_requestTimeoutSeconds));
                try { await HandleAsync(ctx, requestCts.Token).ConfigureAwait(false); }
                finally { _requestSlots.Release(); }
            }, CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx, CancellationToken cancellationToken)
    {
        try
        {
            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            var req = ctx.Request;
            // 헬스 체크 — Promaker 가 전송 전 도달성 확인용.
            if (req.HttpMethod == "GET" && req.Url?.AbsolutePath == "/ping")
            {
                await WriteAsync(ctx, 200, "pong").ConfigureAwait(false);
                return;
            }
            // 모델 다운로드 — 'Agent에서 가져오기 ▸ 네트워크' 가 이 머신 공유폴더의 project.aasx 를 받아간다.
            if (req.HttpMethod == "GET" && req.Url?.AbsolutePath == "/download")
            {
                var aasx = SharedPaths.AasxFilePath;
                if (!File.Exists(aasx))
                {
                    await WriteAsync(ctx, 404, "공유 폴더에 모델(project.aasx)이 없습니다.").ConfigureAwait(false);
                    return;
                }
                var length = new FileInfo(aasx).Length;
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/octet-stream";
                ctx.Response.ContentLength64 = length;
                await using var source = new FileStream(
                    aasx, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous);
                await source.CopyToAsync(ctx.Response.OutputStream, cancellationToken).ConfigureAwait(false);
                ctx.Response.Close();
                Log.Info($"모델 다운로드 응답 — project.aasx ({length} bytes) 전송.");
                return;
            }
            if (req.HttpMethod != "POST" || req.Url?.AbsolutePath != "/upload")
            {
                await WriteAsync(ctx, 404, "not found").ConfigureAwait(false);
                return;
            }

            var maxUpload = _maxUploadBytes;
            if (req.ContentLength64 > maxUpload)
            {
                await WriteAsync(ctx, 413, "upload too large").ConfigureAwait(false);
                return;
            }

            Directory.CreateDirectory(SharedPaths.SharedDirectory);
            Directory.CreateDirectory(SharedPaths.AgentDirectory);

            var stageRoot = Path.Combine(SharedPaths.SharedDirectory, $".agent-upload-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stageRoot);
            AgentSession? stagedSession = null;
            var stagedFiles = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var archivePath = Path.Combine(stageRoot, "payload.zip");
                await using (var archiveFile = new FileStream(
                                 archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 128 * 1024, FileOptions.Asynchronous))
                {
                    await CopyLimitedAsync(req.InputStream, archiveFile, maxUpload, cancellationToken)
                        .ConfigureAwait(false);
                }

                using var zip = ZipFile.OpenRead(archivePath);
                if (zip.Entries.Count is < 1 or > 10)
                    throw new InvalidDataException("Upload archive has an invalid entry count.");
                var allowed = new HashSet<string>(StringComparer.Ordinal)
                {
                    "project.aasx", "PlcConnection.json", "calibration-state.json", "session.json",
                };
                long declaredTotal = 0;
                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName != entry.Name || !allowed.Contains(entry.Name))
                        throw new InvalidDataException($"Unsupported archive entry '{entry.FullName}'.");
                    if (stagedFiles.ContainsKey(entry.Name))
                        throw new InvalidDataException($"Duplicate archive entry '{entry.Name}'.");
                    declaredTotal = checked(declaredTotal + entry.Length);
                    if (declaredTotal > maxUpload)
                        throw new InvalidDataException("Expanded upload exceeds the configured limit.");
                    var entryLimit = entry.Name == "project.aasx" ? maxUpload : 16 * 1024 * 1024;
                    if (entry.Length > entryLimit)
                        throw new InvalidDataException($"Archive entry '{entry.Name}' is too large.");

                    var stagedPath = Path.Combine(stageRoot, entry.Name);
                    await using var source = entry.Open();
                    await using var target = new FileStream(
                        stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        128 * 1024, FileOptions.Asynchronous);
                    await CopyLimitedAsync(source, target, entryLimit, cancellationToken).ConfigureAwait(false);
                    stagedFiles.Add(entry.Name, stagedPath);
                }
                if (!stagedFiles.ContainsKey("project.aasx"))
                    throw new InvalidDataException("Upload archive must contain project.aasx.");
                ValidateAasx(stagedFiles["project.aasx"]);
                if (stagedFiles.TryGetValue("PlcConnection.json", out var stagedPlc)
                    && !PlcConnectionSettings.TryLoadExact(stagedPlc, out _, out var plcError))
                    throw new InvalidDataException(plcError);
                if (stagedFiles.TryGetValue("calibration-state.json", out var stagedCalibration)
                    && !CalibrationState.TryLoadExact(stagedCalibration, out _, out var calibrationError))
                    throw new InvalidDataException(calibrationError);
                if (stagedFiles.TryGetValue("session.json", out var sessionPath))
                {
                    stagedSession = DeserializeSession(await File.ReadAllTextAsync(sessionPath, cancellationToken)
                        .ConfigureAwait(false));
                    ValidateSession(stagedSession);
                }

                var baseSession = stagedSession
                                  ?? AgentSession.TryLoad()
                                  ?? AgentSession.ForCurrentDefaults(requestedBy: "agent-upload");
                var preflightSession = CloneSession(baseSession);
                preflightSession.AasxPath = stagedFiles["project.aasx"];
                preflightSession.PlcConnectionPath = stagedFiles.TryGetValue("PlcConnection.json", out var preflightPlc)
                    ? preflightPlc
                    : SharedPaths.PlcConnectionFilePath;
                if (!MonitoringSupervisor.TryPreflightCandidate(preflightSession, out var preflightError))
                    throw new InvalidDataException($"Agent activation preflight failed: {preflightError}");

                if (stagedSession is not null)
                {
                    stagedSession.AasxPath = SharedPaths.AasxFilePath;
                    stagedSession.PlcConnectionPath = SharedPaths.PlcConnectionFilePath;
                    if (!stagedSession.TrySave(stagedFiles["session.json"]))
                        throw new IOException("Unable to stage the normalized Agent session.");
                    var flagPath = Path.Combine(stageRoot, "active.flag");
                    await File.WriteAllTextAsync(flagPath, stagedSession.ActivatedAtUtc, cancellationToken)
                        .ConfigureAwait(false);
                    stagedFiles.Add("active.flag", flagPath);
                }

                // Validate and stage everything before the shared write lock or live files are touched.
                if (!SharedWriteLock.TryAcquire("Agent-Upload", out var holder))
                {
                    Log.Warn($"업로드 수신 보류 — 공유 쓰기 락 점유 중 (holder={holder.Holder}, pid={holder.Pid}).");
                    await WriteAsync(ctx, 503, $"공유 폴더 쓰기 잠금 중 (점유: {holder.Holder}). 잠시 후 재시도하세요.").ConfigureAwait(false);
                    return;
                }

                try
                {
                    var activation = new List<(string Source, string Destination)>
                    {
                        (stagedFiles["project.aasx"], SharedPaths.AasxFilePath),
                    };
                    if (stagedFiles.TryGetValue("PlcConnection.json", out var plc))
                        activation.Add((plc, SharedPaths.PlcConnectionFilePath));
                    if (stagedFiles.TryGetValue("calibration-state.json", out var calibration))
                        activation.Add((calibration, SharedPaths.CalibrationStateJsonPath));
                    if (stagedSession is not null)
                    {
                        activation.Add((stagedFiles["session.json"], SharedPaths.AgentSessionJsonPath));
                        // active.flag is deliberately the final visible activation marker.
                        activation.Add((stagedFiles["active.flag"], SharedPaths.AgentActiveFlagPath));
                    }
                    ActivateStagedFiles(activation, stageRoot);
                }
                finally
                {
                    SharedWriteLock.Release("Agent-Upload");
                }

                Log.Info(stagedSession is not null
                    ? "모델 업로드 수신 완료 — 검증된 파일 배치 + active.flag(모니터링 시작)."
                    : "모델 업로드 수신 완료 — 검증된 모델 갱신(세션 없음, 활성 상태 불변).");
                await WriteAsync(ctx, 200, stagedSession is not null ? "uploaded-active" : "uploaded-model-only")
                    .ConfigureAwait(false);
            }
            finally
            {
                try { Directory.Delete(stageRoot, recursive: true); } catch { /* best effort */ }
            }
        }
        catch (InvalidDataException ex)
        {
            Log.Warn($"업로드 검증 실패: {ex.Message}");
            try { await WriteAsync(ctx, 400, ex.Message).ConfigureAwait(false); } catch { /* 응답도 실패 */ }
        }
        catch (OperationCanceledException)
        {
            Log.Warn($"업로드/다운로드 요청 시간 초과: {ctx.Request.RemoteEndPoint?.Address}");
            try { await WriteAsync(ctx, 408, "request timeout").ConfigureAwait(false); } catch { /* 연결 종료 */ }
        }
        catch (Exception ex)
        {
            Log.Error("업로드 처리 실패", ex);
            try { await WriteAsync(ctx, 500, "internal error").ConfigureAwait(false); } catch { /* 응답도 실패 */ }
        }
    }

    private bool AllowRequest(HttpListenerContext context)
    {
        var key = context.Request.RemoteEndPoint?.Address.ToString() ?? "unknown";
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

    private static AgentSession DeserializeSession(string json) =>
        JsonSerializer.Deserialize<AgentSession>(json, JsonOpts)
        ?? throw new InvalidDataException("session.json is empty or invalid.");

    private static void ValidateSession(AgentSession session)
    {
        if (!session.TryValidate(out var error)) throw new InvalidDataException(error);
    }

    private static AgentSession CloneSession(AgentSession session) => new()
    {
        AasxPath = session.AasxPath,
        PlcConnectionPath = session.PlcConnectionPath,
        ActivatedAtUtc = session.ActivatedAtUtc,
        RequestedBy = session.RequestedBy,
        RuntimeMode = session.RuntimeMode,
        IsRealPlcConnected = session.IsRealPlcConnected,
        SchemaVersion = session.SchemaVersion,
    };

    private static void ValidateAasx(string path)
    {
        if (!AasxPackageSafety.TryValidate(path, out var packageError))
            throw new InvalidDataException(packageError);
        var store = new DsStore();
        var result = AasxImporter.importIntoStoreWithError(store, path);
        if (result.IsError)
            throw new InvalidDataException($"AASX import validation failed: {result.ErrorValue}");
    }

    private static void ActivateStagedFiles(
        IReadOnlyList<(string Source, string Destination)> files,
        string stageRoot)
    {
        var rollbackRoot = Path.Combine(stageRoot, "rollback");
        Directory.CreateDirectory(rollbackRoot);
        var backups = new List<(string Backup, string Destination)>();
        var activated = new List<string>();
        try
        {
            for (var index = 0; index < files.Count; index++)
            {
                var (_, destination) = files[index];
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (!File.Exists(destination)) continue;
                var backup = Path.Combine(rollbackRoot, index.ToString("D2"));
                File.Copy(destination, backup, overwrite: false);
                backups.Add((backup, destination));
            }
            foreach (var (source, destination) in files)
            {
                File.Move(source, destination, overwrite: true);
                activated.Add(destination);
            }
        }
        catch
        {
            foreach (var (backup, destination) in backups.AsEnumerable().Reverse())
            {
                try { File.Copy(backup, destination, overwrite: true); } catch { }
            }
            var backedUp = backups.Select(item => item.Destination).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var destination in activated.Where(path => !backedUp.Contains(path)))
            {
                try { if (File.Exists(destination)) File.Delete(destination); } catch { }
            }
            throw;
        }
    }

    private static async Task CopyLimitedAsync(
        Stream source,
        Stream destination,
        long limit,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total = checked(total + read);
            if (total > limit) throw new InvalidDataException("Upload exceeds the configured size limit.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
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

    private static int ReadIntEnvironment(string name, int fallback, int minimum, int maximum) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var parsed)
            ? Math.Clamp(parsed, minimum, maximum)
            : fallback;

    private sealed class RequestWindow(DateTimeOffset start)
    {
        public DateTimeOffset Start { get; set; } = start;
        public int Count { get; set; }
    }
}
