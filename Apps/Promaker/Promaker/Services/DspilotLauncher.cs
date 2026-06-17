using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using log4net;

namespace Promaker.Services;

/// <summary>
/// DSPilot 웹 대시보드를 기본 브라우저로 띄우는 헬퍼.
///
/// 설치 판정: `{ProgramFiles}\DualSoft\DSPilot\DSPilot.exe`(또는 x86 경로) 존재 여부.
///   사용자 설정 파일(appsettings.Production.json)은 DSPilot 최초 부팅 전엔 없을 수 있어 설치 마커로 부적절하다.
///
/// 포트(Urls) 해석: DSPilot/Program.cs 의 AddJsonFile 로드 순서(base → Production → Hosting, 마지막이 우선)와
///   동일하게 아래 순서로 "Urls" 첫 정의를 채택한다 — DSPilot 이 어떤 포트를 지정하든 그대로 따라간다.
///     1. appsettings.Hosting.json   — 신버전 설치 스크립트가 사용자가 고른 포트를 기록 (예: "http://*:8080")
///     2. appsettings.Production.json — 구버전 설치본이 Urls 를 보관하던 곳
///     3. appsettings.json            — 번들 기본값(보통 Urls 없음)
///   세 파일 모두에 Urls 가 없어도 DSPilot.exe 가 있으면 기본 포트(80, http://localhost)로 폴백한다.
/// DSPilot 인스톨러가 `*` 와이드카드 호스트 + 포트로 Urls 를 기록하므로 host 는 localhost 로 치환한다.
/// </summary>
public static class DspilotLauncher
{
    private static readonly ILog Log = LogManager.GetLogger("DspilotLauncher");

    /// <summary>DSPilot 설치 여부 — Program Files 후보 경로 중 하나라도 DSPilot.exe 존재 시 true.</summary>
    public static bool IsInstalled()
    {
        foreach (var dir in EnumerateInstallDirs())
            if (File.Exists(Path.Combine(dir, "DSPilot.exe"))) return true;
        return false;
    }

    /// <summary>DSPilot/Program.cs 의 AddJsonFile 순서상 마지막에 로드된 파일이 이긴다.
    /// 따라서 Urls 는 역순(Hosting → Production → base)으로 첫 정의를 채택한다.</summary>
    private static readonly string[] ConfigFileNamesByPrecedence =
    {
        "appsettings.Hosting.json",
        "appsettings.Production.json",
        "appsettings.json",
    };

    /// <summary>VS dev 인스턴스가 사용하는 DSPilot 포트 — <c>Apps/DSPilot/DSPilot/Properties/launchSettings.json</c>
    /// 의 applicationUrl 과 동기 유지. 이 포트가 listening 이면 설치본보다 우선해서 browser 로 띄운다.</summary>
    private const int DevDspilotPort = 58493;

    /// <summary>DSPilot 의 접속 URL. 우선순위:
    /// (1) localhost:<see cref="DevDspilotPort"/> 가 listening 이면 거기 (dev 디버깅 인스턴스 가정).
    /// (2) 설치본의 appsettings.Production.json 의 "Urls" 필드.
    /// 둘 다 없으면 null.</summary>
    public static string? ResolveUrl()
    {
        if (IsTcpListening(System.Net.IPAddress.Loopback, DevDspilotPort))
        {
            var devUrl = $"https://localhost:{DevDspilotPort}";
            Log.Info($"DSPilot dev 인스턴스 감지 — {devUrl} 사용");
            return devUrl;
        }
        Log.Debug($"DSPilot dev 포트 {DevDspilotPort} 비활성 — 설치본 lookup");

        foreach (var dir in EnumerateInstallDirs())
        {
            foreach (var fileName in ConfigFileNamesByPrecedence)
            {
                var candidate = Path.Combine(dir, fileName);
                try
                {
                    if (!File.Exists(candidate)) continue;
                    var url = TryReadUrlsField(candidate);
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        Log.Debug($"DSPilot Urls 채택: {candidate} → {url}");
                        return url;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"DSPilot 설정 읽기 실패 ({candidate}): {ex.Message}");
                }
            }
        }

        // 설정 파일 어디에도 Urls 가 없지만 DSPilot.exe 가 있으면 설치는 된 것 → 기본 포트(80) 폴백.
        // (정상 설치본은 Hosting.json 에 Urls 가 있어 여기까지 오지 않는다. 손상/부분설치 안전망.)
        if (IsInstalled())
        {
            Log.Info("DSPilot 설치 확인됨(설정에 Urls 없음) — 기본 http://localhost 폴백");
            return "http://localhost";
        }
        return null;
    }

    /// <summary>IPv4 loopback 직접 지정 — "localhost" 사용 시 IPv6(::1) 우선 resolve 로 인해
    /// 0.0.0.0 (IPv4-only) bind 한 Kestrel 을 놓치는 사례 방지.</summary>
    private static bool IsTcpListening(System.Net.IPAddress address, int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var connectTask = client.ConnectAsync(address, port);
            var ok = connectTask.Wait(500);
            if (!ok)
            {
                // 타임아웃으로 버려지는 connect Task — using dispose 가 소켓을 닫으면 그 Task 가
                // fault 하는데, 예외를 관찰하지 않으면 GC 때 Unobserved task exception 으로 표면화
                // (실기: DSPilot 미기동 probe 직후 ERROR 1건). 예외만 소비하고 버린다.
                _ = connectTask.ContinueWith(
                    t => _ = t.Exception,
                    System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
                Log.Debug($"TCP probe {address}:{port} timeout (500ms)");
            }
            return ok;
        }
        catch (Exception ex)
        {
            Log.Debug($"TCP probe {address}:{port} 실패: {ex.GetType().Name} {ex.Message}");
            return false;
        }
    }

    /// <summary>DSPilot 이 설치되어 있으면 기본 브라우저로 띄움. 미설치면 안내 다이얼로그 표시 (suppress 시 SimLog 만).</summary>
    public static void Open()
    {
        var url = ResolveUrl();
        if (url is null)
        {
            Log.Info("DSPilot 미설치 — 브라우저 실행 건너뜀");
            Promaker.Dialogs.DspilotMissingNoticeDialog.Show();
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            Log.Info($"DSPilot 브라우저 실행: {url}");
        }
        catch (Exception ex)
        {
            Log.Warn($"DSPilot 브라우저 실행 실패 ({url}): {ex.Message}");
        }
    }

    /// <summary>DSPilot 설치 후보 디렉터리 — `{ProgramFiles}\DualSoft\DSPilot` 및 (다르면) x86 경로.</summary>
    private static System.Collections.Generic.IEnumerable<string> EnumerateInstallDirs()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(pf))
            yield return Path.Combine(pf, "DualSoft", "DSPilot");
        if (!string.IsNullOrEmpty(pfx86) && !string.Equals(pf, pfx86, StringComparison.OrdinalIgnoreCase))
            yield return Path.Combine(pfx86, "DualSoft", "DSPilot");
    }

    private static string? TryReadUrlsField(string jsonPath)
    {
        using var stream = File.OpenRead(jsonPath);
        using var doc = JsonDocument.Parse(stream);
        if (!doc.RootElement.TryGetProperty("Urls", out var urlsEl)) return null;
        if (urlsEl.ValueKind != JsonValueKind.String) return null;
        var raw = urlsEl.GetString();
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // "Urls" 는 ";" 로 다중 바인딩 가능 — 첫 http(s) 항목만 사용.
        var first = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];

        // 호스트 와이드카드(`*`, `+`, `0.0.0.0`) → localhost 치환.
        var normalized = Regex.Replace(first,
            @"://(\*|\+|0\.0\.0\.0)(?=[:/]|$)",
            "://localhost",
            RegexOptions.IgnoreCase);

        // 포트 80(http) / 443(https) 는 URL 에서 생략 — 사용자가 본 desktop shortcut 과 일치.
        normalized = Regex.Replace(normalized, @"^http://([^/:]+):80(?=/|$)",  "http://$1",  RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"^https://([^/:]+):443(?=/|$)", "https://$1", RegexOptions.IgnoreCase);

        return normalized;
    }
}
