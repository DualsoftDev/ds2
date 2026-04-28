using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using log4net;

namespace Promaker.Services;

/// <summary>
/// SystemType 별 FBTagMap 디폴트 값을 사용자별 AppData 디렉토리 안에 JSON 파일로 보관한다.
///   <c>%APPDATA%\Dualsoft\Promaker\FBTagMap\&lt;SystemType&gt;.json</c>
/// 첫 실행 시 (디렉토리 비어 있으면) C# 코드의 factory 디폴트로 시드 생성.
/// 이후에는 JSON 파일이 truth source — 사용자가 외부에서 편집 가능.
/// </summary>
public static class FBTagMapDefaultsRepository
{
    private static readonly ILog Log = LogManager.GetLogger("FBTagMapDefaultsRepository");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null, // PascalCase 보존 (DTO 와 동일)
    };

    private static bool _seeded;

    /// <summary>
    /// 디폴트 디렉토리 경로. 환경변수 <c>DS2_FBTAGMAP_DIR</c> 로 override 가능 (테스트/배포용).
    /// </summary>
    public static string GetDefaultsDirectory()
    {
        var overridePath = Environment.GetEnvironmentVariable("DS2_FBTAGMAP_DIR");
        if (!string.IsNullOrWhiteSpace(overridePath)) return overridePath;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "Dualsoft", "Promaker", "FBTagMap");
    }

    private static string GetFilePath(string sysType) =>
        Path.Combine(GetDefaultsDirectory(), Sanitize(sysType) + ".json");

    private static string Sanitize(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = s.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
        return new string(chars);
    }

    /// <summary>SystemType 디폴트 JSON 로드. 파일 없거나 파싱 실패 시 null.</summary>
    public static FBTagMapPresetDto? Load(string sysType)
    {
        try
        {
            var path = GetFilePath(sysType);
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<FBTagMapPresetDto>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            Log.Warn($"FBTagMap 디폴트 로드 실패 ({sysType}): {ex.Message}");
            return null;
        }
    }

    /// <summary>SystemType 디폴트 JSON 저장 (덮어씀).</summary>
    public static void Save(string sysType, FBTagMapPresetDto dto)
    {
        try
        {
            var dir = GetDefaultsDirectory();
            Directory.CreateDirectory(dir);
            var path = GetFilePath(sysType);
            File.WriteAllText(path, JsonSerializer.Serialize(dto, JsonOpts));
        }
        catch (Exception ex)
        {
            Log.Warn($"FBTagMap 디폴트 저장 실패 ({sysType}): {ex.Message}");
        }
    }

    /// <summary>
    /// 첫 실행 시 1회 시드. 디렉토리가 없거나 비어있으면 factory 디폴트로 모든 SystemType JSON 생성.
    /// 디렉토리에 JSON 이 1개 이상 있으면 시드 건너뜀 (사용자 편집 보존).
    /// </summary>
    public static void EnsureSeeded(Func<string, string?, FBTagMapPresetDto> factory, IEnumerable<(string sysType, string? defaultFb)> entries)
    {
        if (_seeded) return;
        _seeded = true;

        try
        {
            var dir = GetDefaultsDirectory();
            Directory.CreateDirectory(dir);

            var existing = Directory.EnumerateFiles(dir, "*.json").Any();
            if (existing) return; // 이미 시드됨 — 사용자 편집 보존

            foreach (var (sysType, defaultFb) in entries)
            {
                if (string.IsNullOrWhiteSpace(sysType)) continue;
                var dto = factory(sysType, defaultFb);
                Save(sysType, dto);
            }
            Log.Info($"FBTagMap 디폴트 JSON 시드 완료 → {dir}");
        }
        catch (Exception ex)
        {
            Log.Warn($"FBTagMap 디폴트 시드 실패: {ex.Message}");
        }
    }

    /// <summary>모든 디폴트 파일 강제 재생성 (사용자가 "팩토리 리셋" 명령 시 호출).</summary>
    public static void ResetAll(Func<string, string?, FBTagMapPresetDto> factory, IEnumerable<(string sysType, string? defaultFb)> entries)
    {
        try
        {
            var dir = GetDefaultsDirectory();
            if (Directory.Exists(dir))
                foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
                    File.Delete(f);
            _seeded = false;
            EnsureSeeded(factory, entries);
        }
        catch (Exception ex)
        {
            Log.Warn($"FBTagMap 디폴트 리셋 실패: {ex.Message}");
        }
    }
}
