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
    private static readonly string ConfigFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Dualsoft", "Promaker", "PlcConfig.txt");

    public static PlcSettings Settings => Load();

    public static void Save(string ioTemplateDirPath, string xgiTemplatePath, string xg5000ExePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigFile);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllLines(ConfigFile,
            [
                $"IoTemplateDirPath={ioTemplateDirPath}",
                $"XgiTemplatePath={xgiTemplatePath}",
                $"Xg5000ExePath={xg5000ExePath}",
            ]);
        }
        catch { }
    }

    private static PlcSettings Load()
    {
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

        return new PlcSettings
        {
            IoTemplateDirPath = dict.GetValueOrDefault("IoTemplateDirPath", ""),
            XgiTemplatePath   = dict.GetValueOrDefault("XgiTemplatePath",   ""),
            Xg5000ExePath     = dict.GetValueOrDefault("Xg5000ExePath",     ""),
        };
    }
}

public class PlcSettings
{
    public string IoTemplateDirPath { get; init; } = "";
    public string XgiTemplatePath   { get; init; } = "";

    /// <summary>유효한 IOList 템플릿 폴더 경로 (비어 있으면 TemplateManager 기본값)</summary>
    public string EffectiveIoTemplateDirPath =>
        !string.IsNullOrWhiteSpace(IoTemplateDirPath)
            ? IoTemplateDirPath
            : TemplateManager.TemplatesFolderPath;

    /// <summary>유효한 XGI 템플릿 파일 경로 (비어 있으면 AppBase/Template/XGI_Template.xml)</summary>
    public string EffectiveXgiTemplatePath =>
        !string.IsNullOrWhiteSpace(XgiTemplatePath)
            ? XgiTemplatePath
            : TemplateManager.XgiTemplatePath;

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
