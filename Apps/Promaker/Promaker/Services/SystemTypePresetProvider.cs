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
/// 저장 형식 (ProjectPropertiesDialog 와 동일):
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
            if (idx <= 0) continue;

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

    /// <summary>특정 SystemType 의 API 이름 목록. 프리셋에 없으면 빈 배열.</summary>
    public static string[] GetApiNames(string systemType)
    {
        var entry = GetEntries()
            .FirstOrDefault(e => string.Equals(e.SystemType, systemType, StringComparison.OrdinalIgnoreCase));
        return entry.ApiNames ?? Array.Empty<string>();
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
                if (parsed != null && parsed.Length > 0) return parsed;
            }
        }
        catch { /* fall through */ }

        return Ds2.Core.Store.DevicePresets.DefaultMappingStrings;
    }
}
