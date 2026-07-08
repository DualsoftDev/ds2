// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Collections.Concurrent;
using System.Diagnostics;
using DSPilot.Models;

namespace DSPilot.Services;

/// <summary>
/// CCTV 스냅샷(정지 프레임) 획득 서비스. 싱글톤.
///
/// MediaMTX 가 재게시하는 RTSP(rtsp://localhost:8554/{slug})에서 ffmpeg 원샷 프로세스로 1프레임을 뽑는다.
/// - ffmpeg 는 상주하지 않는다: 요청 시 실행 → 1프레임 → 즉시 종료 (서비스 등록·방화벽 설정 불필요).
/// - sourceOnDemand: 리더(ffmpeg)가 붙는 순간 MediaMTX 가 원본 카메라에 연결하므로 기동 지연(수 초)이
///   있을 수 있다 → <see cref="GrabTimeout"/> 에 여유를 둔다.
/// - 카메라별 single-flight + 짧은 TTL 캐시: 동시/연속 요청이 ffmpeg 프로세스 1개로 흡수된다
///   (PlcPingService 의 TTL single-flight 패턴).
/// - 라이브 그랩 실패 시 대체(폴백) 이미지를 베이스로 폴백 — 그것도 없으면 실패(null bytes + 사유).
/// </summary>
public class CctvSnapshotService
{
    /// <summary>같은 카메라 연속 요청을 흡수하는 프레임 캐시 수명. 실패 결과도 같은 수명으로 캐시(스탬피드 방지).</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(3);
    /// <summary>ffmpeg 1프레임 그랩 상한. sourceOnDemand 기동(원본 카메라 RTSP 협상) 지연 포함.</summary>
    private static readonly TimeSpan GrabTimeout = TimeSpan.FromSeconds(15);

    private readonly AppSettingsService _settings;
    private readonly ILogger<CctvSnapshotService> _logger;
    private readonly string _fallbackDir;

    // slug → (그랩 태스크, 시작 시각). 진행 중이거나 TTL 내 완료면 공유(single-flight).
    private sealed record Inflight(Task<byte[]?> Task, DateTime StartedUtc);
    private readonly Dictionary<string, Inflight> _inflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    // slug → 마지막 라이브 그랩 실패 사유 (응답 진단용 — 성공 시 제거).
    private readonly ConcurrentDictionary<string, string> _lastError = new(StringComparer.OrdinalIgnoreCase);

    public CctvSnapshotService(AppSettingsService settings, IWebHostEnvironment env, ILogger<CctvSnapshotService> logger)
    {
        _settings = settings;
        _logger = logger;
        // CctvFallbackImageService 와 동일 규칙 (WebRootPath null 폴백 포함).
        var webRoot = env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot");
        _fallbackDir = Path.Combine(webRoot, "uploads", "cctv-fallbacks");
    }

    /// <summary>
    /// 카메라의 현재 프레임을 획득. 라이브(RTSP) 우선, 실패 시 대체 이미지 폴백.
    /// bytes=null 이면 완전 실패 — error 에 사유(ffmpeg 부재/스트림 실패 등).
    /// </summary>
    public async Task<(byte[]? bytes, bool fromLive, string? error)> GetFrameAsync(CctvCamera cam, CancellationToken ct)
    {
        byte[]? live = null;
        string? error = null;

        if (!string.IsNullOrWhiteSpace(cam.RtspUrl) && !string.IsNullOrWhiteSpace(cam.Slug))
        {
            live = await GrabSharedAsync(cam.Slug).WaitAsync(ct);
            if (live is not null) return (live, true, null);
            _lastError.TryGetValue(cam.Slug, out error);
        }
        else if (string.IsNullOrWhiteSpace(cam.RtspUrl))
        {
            error = "카메라에 RTSP 주소가 없습니다(대체 이미지 전용).";
        }

        var fb = ReadFallbackBytes(cam.FallbackImage);
        if (fb is not null) return (fb, false, error);

        return (null, false, error ?? "프레임을 획득할 수 없습니다.");
    }

    /// <summary>single-flight: 진행 중이거나 TTL 내 완료된 그랩 태스크를 공유, 없으면 새로 시작.</summary>
    private Task<byte[]?> GrabSharedAsync(string slug)
    {
        lock (_gate)
        {
            if (_inflight.TryGetValue(slug, out var e)
                && (!e.Task.IsCompleted || DateTime.UtcNow - e.StartedUtc < CacheTtl))
                return e.Task;

            var task = Task.Run(() => GrabOnceAsync(slug));
            _inflight[slug] = new Inflight(task, DateTime.UtcNow);
            return task;
        }
    }

    /// <summary>ffmpeg 원샷 실행으로 rtsp://{host}:{rtspPort}/{slug} 에서 JPEG 1프레임을 뽑는다.</summary>
    private async Task<byte[]?> GrabOnceAsync(string slug)
    {
        var cctv = _settings.LoadSettings().Cctv;
        var ffmpeg = ResolveFfmpegPath(cctv);
        if (ffmpeg is null)
        {
            _lastError[slug] = "ffmpeg 실행 파일을 찾을 수 없습니다({앱}\\ffmpeg\\ffmpeg.exe 또는 PATH, 설정 Cctv.FfmpegPath).";
            return null;
        }

        // MediaMTX 는 같은 호스트 — API URL 의 host 를 재사용 (기본 localhost).
        var host = "127.0.0.1";
        if (Uri.TryCreate(cctv.MediaMtxApiUrl, UriKind.Absolute, out var api) && !string.IsNullOrEmpty(api.Host))
            host = api.Host;
        var url = $"rtsp://{host}:{cctv.RtspPort}/{slug}";

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // -frames:v 1 → 첫 디코드 프레임만 mjpeg 로 stdout 에 쓰고 종료. TCP 강제(UDP 손실/방화벽 회피).
        foreach (var a in new[]
        {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-rtsp_transport", "tcp", "-i", url,
            "-frames:v", "1", "-f", "image2pipe", "-vcodec", "mjpeg", "-q:v", "4", "-",
        }) psi.ArgumentList.Add(a);

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) { _lastError[slug] = "ffmpeg 프로세스를 시작하지 못했습니다."; return null; }

            using var timeoutCts = new CancellationTokenSource(GrabTimeout);
            var stdout = new MemoryStream();
            var copyTask = proc.StandardOutput.BaseStream.CopyToAsync(stdout, timeoutCts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token);
                await copyTask;
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* 이미 종료 */ }
                _lastError[slug] = $"프레임 획득 시간 초과({GrabTimeout.TotalSeconds:0}s) — 카메라/스트림 응답 없음.";
                return null;
            }

            if (proc.ExitCode != 0 || stdout.Length == 0)
            {
                var err = "";
                try { err = (await stderrTask).Trim(); } catch { /* 진단용 — 실패해도 무시 */ }
                if (err.Length > 300) err = err[..300];
                _lastError[slug] = string.IsNullOrEmpty(err)
                    ? $"ffmpeg 종료 코드 {proc.ExitCode}, 프레임 없음."
                    : err;
                _logger.LogWarning("CCTV 스냅샷 실패({Slug}): {Err}", slug, _lastError[slug]);
                return null;
            }

            _lastError.TryRemove(slug, out _);
            return stdout.ToArray();
        }
        catch (Exception ex) // ffmpeg 부재(Win32Exception) 포함
        {
            _lastError[slug] = $"ffmpeg 실행 실패: {ex.Message}";
            _logger.LogWarning(ex, "CCTV 스냅샷 ffmpeg 실행 실패({Slug})", slug);
            return null;
        }
    }

    /// <summary>
    /// ffmpeg 경로 결정: ① 설정 오버라이드 ② 앱 폴더 동봉(ffmpeg\ffmpeg[.exe]) ③ PATH.
    /// PATH 후보는 존재 검증 없이 반환 — 실행 시점 Win32Exception 을 실패로 처리.
    /// </summary>
    private static string? ResolveFfmpegPath(CctvSettings cctv)
    {
        if (!string.IsNullOrWhiteSpace(cctv.FfmpegPath))
            return File.Exists(cctv.FfmpegPath) ? cctv.FfmpegPath : null;

        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg", exe);
        if (File.Exists(bundled)) return bundled;

        return "ffmpeg";
    }

    /// <summary>
    /// 대체 이미지 URL("/uploads/cctv-fallbacks/cam1.jpg?v=...")을 파일로 읽는다. 없으면 null.
    /// 파일명만 취해 디렉터리 밖 접근을 차단.
    /// </summary>
    private byte[]? ReadFallbackBytes(string? fallbackUrl)
    {
        if (string.IsNullOrWhiteSpace(fallbackUrl)) return null;
        var url = fallbackUrl;
        var q = url.IndexOf('?');
        if (q >= 0) url = url[..q];
        var name = Path.GetFileName(url.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(name)) return null;
        var path = Path.Combine(_fallbackDir, name);
        try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CCTV 대체 이미지 읽기 실패: {Path}", path);
            return null;
        }
    }
}
