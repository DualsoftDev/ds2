using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Promaker.Services;

/// <summary>
/// 프로젝트 속성의 "SystemType 프리셋" (<c>systemTypePreset.json</c>) 을 단일 진실원으로 노출.
/// TAG Wizard 의 SystemType 목록, 신호 템플릿 seed, 임시 디렉토리 emit 등에서 공통 사용.
///
/// 저장 형식 (ApplicationSettingsDialog 의 프리셋 탭과 동일):
///   - JSON string[] — 각 항목 "ApiList:SystemType" (예: "ADV;RET:Cylinder")
///   - 파일 없거나 빈 경우 <see cref="Ds2.Core.Store.DevicePresets.DefaultMappingStrings"/> 폴백
/// </summary>
public static class SystemTypePresetProvider
{
    private static readonly string PresetFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Dualsoft", "Promaker", "systemTypePreset", "systemTypePreset.json");

    /// <summary>(SystemType, ApiNames) 목록을 설정 프리셋에서 로드. 파일 없으면 DevicePresets 폴백.</summary>
    public static IReadOnlyList<(string SystemType, string[] ApiNames)> GetEntries()
    {
        var raw = LoadMappingStrings();
        var result = new List<(string, string[])>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mapping in raw)
        {
            if (string.IsNullOrWhiteSpace(mapping)) continue;
            var idx = mapping.LastIndexOf(':');
            if (idx < 0) continue;

            var apiList    = mapping.Substring(0, idx).Trim();
            var systemType = mapping.Substring(idx + 1).Trim();
            if (string.IsNullOrWhiteSpace(systemType)) continue;
            if (!seen.Add(systemType)) continue;

            var apis = apiList
                .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(a => a.Trim())
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .ToArray();
            result.Add((systemType, apis));
        }
        return result;
    }

    /// <summary>설정 프리셋의 SystemType 이름만 정렬해서 반환.</summary>
    public static IReadOnlyList<string> GetSystemTypes() =>
        GetEntries()
            .Select(e => e.SystemType)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// 특정 SystemType 의 API 이름 목록.
    ///   • 정확 일치 → 그 entry 반환
    ///   • 일치 없고 'Cylinder_5' 같은 numeric suffix 면 'Cylinder_#' 같은 템플릿 entry 로 fallback
    /// 프리셋에 없으면 빈 배열.
    /// </summary>
    public static string[] GetApiNames(string systemType)
    {
        if (string.IsNullOrEmpty(systemType)) return Array.Empty<string>();

        var entries = GetEntries();
        // (1) 정확 일치
        var exact = entries.FirstOrDefault(e =>
            string.Equals(e.SystemType, systemType, StringComparison.OrdinalIgnoreCase));
        if (exact.ApiNames != null && exact.ApiNames.Length > 0)
            return exact.ApiNames;

        // (2) '#' 템플릿 fallback — 'Cylinder_5' → 'Cylinder_#' 매칭
        foreach (var e in entries)
        {
            if (string.IsNullOrEmpty(e.SystemType) || !e.SystemType.Contains('#')) continue;
            var pattern = "^" + System.Text.RegularExpressions.Regex.Escape(e.SystemType).Replace("\\#", @"\d+") + "$";
            if (System.Text.RegularExpressions.Regex.IsMatch(systemType, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return e.ApiNames ?? Array.Empty<string>();
        }
        return Array.Empty<string>();
    }

    // ── 내부 로더 ────────────────────────────────────────────────────────────

    private static string[] LoadMappingStrings()
    {
        try
        {
            if (File.Exists(PresetFilePath))
            {
                var json = File.ReadAllText(PresetFilePath);
                var parsed = JsonSerializer.Deserialize<string[]>(json);
                if (parsed != null && parsed.Length > 0)
                    return MergeWithDefaults(parsed);
            }
        }
        catch { /* fall through */ }

        return BuildDefaultMappingStrings();
    }

    /// <summary>사용자 저장본 + DevicePresets 신규 SystemType 합집합 (저장본 순서 우선).</summary>
    private static string[] MergeWithDefaults(string[] saved)
    {
        var result = new List<string>(saved);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in saved)
        {
            var idx = m.LastIndexOf(':');
            if (idx >= 0) seen.Add(m.Substring(idx + 1).Trim());
        }
        foreach (var def in BuildDefaultMappingStrings())
        {
            var idx = def.LastIndexOf(':');
            var sysType = idx >= 0 ? def.Substring(idx + 1).Trim() : "";
            if (!string.IsNullOrEmpty(sysType) && seen.Add(sysType))
                result.Add(def);
        }
        return result.ToArray();
    }

    /// <summary>
    /// "ApiList:SystemType" 기본 매핑 문자열 배열을 <see cref="Ds2.Core.Store.DevicePresets.Entries()"/> 에서 생성.
    /// Cylinder_1..10 같은 동일 prefix 의 numbered SystemType 은 첫 등장 위치에서 단일 템플릿
    /// "ADV;RET:Cylinder_#" 으로 축약 — '#' 은 AddCall 시 ApiCall 개수로 치환.
    /// </summary>
    public static string[] BuildDefaultMappingStrings()
    {
        var result = new List<string>();
        bool cylinderEmitted = false;
        foreach (var t in Ds2.Core.Store.DevicePresets.Entries())
        {
            var model = t.Item1;
            var apiList = t.Item2 ?? "";
            if (model.StartsWith("Cylinder_", StringComparison.Ordinal))
            {
                if (cylinderEmitted) continue;
                cylinderEmitted = true;
                result.Add($"{apiList}:Cylinder_#");
            }
            else
            {
                result.Add($"{apiList}:{model}");
            }
        }
        return result.ToArray();
    }
}
