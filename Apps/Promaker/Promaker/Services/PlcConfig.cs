using System;
using System.Collections.Generic;
using System.IO;

namespace Promaker.Services;

/// <summary>
/// PLC 생성 경로 설정 — AppData 폴더의 PlcConfig.txt 하나로 저장 (key=value 형식).
/// 값이 비어 있으면 TemplateManager 기본값을 사용합니다.
/// </summary>
public static class PlcConfig
{
    private static string ConfigFile => SettingsPaths.PlcConfig;

    public static PlcSettings Settings => Load();

    public static void Save(string xgiTemplatePath, string xg5000ExePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigFile);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // 빈 값 허용 안 함 — 어느 한쪽이라도 비어있으면 디폴트로 복원해 저장.
            var xgi = string.IsNullOrWhiteSpace(xgiTemplatePath) ? ResolveDefaultXgiTemplatePath() : xgiTemplatePath;
            var xg5 = string.IsNullOrWhiteSpace(xg5000ExePath)   ? ResolveDefaultXg5000ExePath()   : xg5000ExePath;

            File.WriteAllLines(ConfigFile,
            [
                $"XgiTemplatePath={xgi}",
                $"Xg5000ExePath={xg5}",
            ]);
        }
        catch { }
    }

    /// <summary>
    /// PlcConfig.txt 가 없으면 AAStoPLC.dll 의 임베디드 리소스 XGI_Template.xml 을
    /// AppData\Dualsoft\Promaker\PlcTemplate\ 로 추출하고, 그 경로를 기본값으로 한 PlcConfig.txt 작성.
    /// 사용자는 이후 AppData 의 사본만 편집 — 앱 업데이트로 덮어써지지 않음.
    /// </summary>
    private static void EnsureInitialized()
    {
        if (File.Exists(ConfigFile)) return;

        try
        {
            Directory.CreateDirectory(SettingsPaths.PlcTemplateDir);
            XgiTemplateExtractor.ExtractIfMissing(SettingsPaths.DefaultXgiTemplate);

            // PlcConfig 신규 생성 — 기본 경로를 AppData 사본으로 고정.
            Save(xgiTemplatePath: SettingsPaths.DefaultXgiTemplate, xg5000ExePath: "");
        }
        catch { /* 추출/생성 실패 시 EffectiveXgiTemplatePath 의 fallback 으로 진행 */ }
    }

    private static PlcSettings Load()
    {
        EnsureInitialized();

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(ConfigFile))
            {
                foreach (var line in File.ReadAllLines(ConfigFile))
                {
                    var idx = line.IndexOf('=');
                    if (idx > 0)
                        dict[line[..idx].Trim()] = line[(idx + 1)..].Trim();
                }
            }
        }
        catch { }

        // 어떤 값이라도 비어있으면 즉시 디폴트로 채우고 다시 저장 — 빈 값은 허용하지 않음.
        var xgi = dict.GetValueOrDefault("XgiTemplatePath", "");
        var xg5 = dict.GetValueOrDefault("Xg5000ExePath",   "");
        var origXgi = xgi;
        var origXg5 = xg5;

        if (string.IsNullOrWhiteSpace(xgi))
            xgi = ResolveDefaultXgiTemplatePath();
        if (string.IsNullOrWhiteSpace(xg5))
            xg5 = ResolveDefaultXg5000ExePath();

        if (!string.Equals(origXgi, xgi, StringComparison.Ordinal)
         || !string.Equals(origXg5, xg5, StringComparison.Ordinal))
        {
            Save(xgi, xg5);
        }

        return new PlcSettings
        {
            XgiTemplatePath = xgi,
            Xg5000ExePath   = xg5,
        };
    }

    /// <summary>XGI 템플릿 디폴트 — AppData 사본. 없으면 임베디드 리소스에서 즉시 추출 후 그 경로 반환.</summary>
    private static string ResolveDefaultXgiTemplatePath()
    {
        if (!File.Exists(SettingsPaths.DefaultXgiTemplate))
            XgiTemplateExtractor.ExtractIfMissing(SettingsPaths.DefaultXgiTemplate);
        return SettingsPaths.DefaultXgiTemplate;
    }

    /// <summary>XG5000.exe 디폴트 — 알려진 설치 경로에서 실재하는 첫 항목, 없으면 표준 경로 문자열.</summary>
    private static string ResolveDefaultXg5000ExePath()
    {
        var candidates = new[] { @"C:\XG5000\XG5000.exe" };
        return Array.Find(candidates, File.Exists) ?? candidates[0];
    }
}

public class PlcSettings
{
    public string XgiTemplatePath   { get; init; } = "";

    /// <summary>
    /// 유효한 XGI 템플릿 경로 —
    ///   (1) 사용자 설정값 (XgiTemplatePath)
    ///   (2) AppData\PlcTemplate\XGI_Template.xml — 없으면 AAStoPLC.dll 임베디드 리소스에서 추출.
    /// </summary>
    public string EffectiveXgiTemplatePath
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(XgiTemplatePath))
                return XgiTemplatePath;
            if (!File.Exists(SettingsPaths.DefaultXgiTemplate))
                XgiTemplateExtractor.ExtractIfMissing(SettingsPaths.DefaultXgiTemplate);
            return SettingsPaths.DefaultXgiTemplate;
        }
    }

    public string Xg5000ExePath { get; init; } = "";

    /// <summary>유효한 XG5000 실행 파일 경로 (비어 있으면 기본 설치 경로에서 탐색)</summary>
    public string EffectiveXg5000ExePath
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Xg5000ExePath) && File.Exists(Xg5000ExePath))
                return Xg5000ExePath;
            // 기본 설치 경로 탐색
            var candidates = new[]
            {
                @"C:\XG5000\XG5000.exe",
            };
            return Array.Find(candidates, File.Exists) ?? "";
        }
    }
}
