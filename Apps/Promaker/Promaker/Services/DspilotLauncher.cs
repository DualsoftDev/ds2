using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using log4net;

namespace Promaker.Services;

/// <summary>
/// DSPilot 웹 대시보드를 기본 브라우저로 띄우는 헬퍼.
/// URL 해석 우선순위:
///   1. `{ProgramFiles}\DualSoft\DSPilot\appsettings.Production.json` 의 "Urls" 필드 (예: "http://*:80")
///   2. `{ProgramFilesX86}\DualSoft\DSPilot\appsettings.Production.json` (32-bit 머신/잔여 설치)
/// 두 경로 모두 누락이면 DSPilot 미설치로 판단 — fallback URL 없이 null 반환.
/// DSPilot 인스톨러가 `*` 와이드카드 호스트 + 포트로 Urls 를 기록하므로 host 는 localhost 로 치환한다.
/// </summary>
public static class DspilotLauncher
{
    private static readonly ILog Log = LogManager.GetLogger("DspilotLauncher");

    /// <summary>DSPilot 설치 여부 — Program Files 후보 경로 중 하나라도 appsettings.Production.json 존재 시 true.</summary>
    public static bool IsInstalled()
    {
        foreach (var candidate in EnumerateConfigPaths())
            if (File.Exists(candidate)) return true;
        return false;
    }

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

        foreach (var candidate in EnumerateConfigPaths())
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var url = TryReadUrlsField(candidate);
                if (!string.IsNullOrWhiteSpace(url))
                    return url;
            }
            catch (Exception ex)
            {
                Log.Warn($"DSPilot 설정 읽기 실패 ({candidate}): {ex.Message}");
            }
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

    private static System.Collections.Generic.IEnumerable<string> EnumerateConfigPaths()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(pf))
            yield return Path.Combine(pf, "DualSoft", "DSPilot", "appsettings.Production.json");
        if (!string.IsNullOrEmpty(pfx86) && !string.Equals(pf, pfx86, StringComparison.OrdinalIgnoreCase))
            yield return Path.Combine(pfx86, "DualSoft", "DSPilot", "appsettings.Production.json");
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
