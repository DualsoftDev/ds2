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
///   3. fallback: `http://localhost` (DSPilot Inno Setup 의 기본 포트 80 가정)
/// DSPilot 인스톨러가 `*` 와이드카드 호스트 + 포트로 Urls 를 기록하므로 host 는 localhost 로 치환한다.
/// </summary>
public static class DspilotLauncher
{
    private static readonly ILog Log = LogManager.GetLogger("DspilotLauncher");

    private const string DefaultUrl = "http://localhost";

    public static string ResolveUrl()
    {
        foreach (var candidate in EnumerateConfigPaths())
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var url = TryReadUrlsField(candidate);
                if (!string.IsNullOrWhiteSpace(url))
                    return url!;
            }
            catch (Exception ex)
            {
                Log.Warn($"DSPilot 설정 읽기 실패 ({candidate}): {ex.Message}");
            }
        }
        return DefaultUrl;
    }

    public static void Open()
    {
        var url = ResolveUrl();
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
