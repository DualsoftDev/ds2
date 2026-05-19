using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Ds2.Editor;

namespace Promaker.Services;

/// <summary>
/// 프로젝트 속성의 "SystemType 프리셋" (<c>systemTypePreset.json</c>) 을 단일 진실원으로 노출.
/// 파싱/병합/조회 결정 로직은 F# <see cref="SystemTypePreset"/> 에 위임.
/// 본 클래스는 file IO + JSON 역직렬화 + C# 친화 시그니처 adapter 만 담당.
/// </summary>
public static class SystemTypePresetProvider
{
    private static readonly string PresetFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Dualsoft", "Promaker", "systemTypePreset", "systemTypePreset.json");

    /// <summary>(SystemType, ApiNames) 목록을 설정 프리셋에서 로드. 파일 없으면 DevicePresets 폴백.</summary>
    public static IReadOnlyList<(string SystemType, string[] ApiNames)> GetEntries() =>
        SystemTypePreset.parseEntries(LoadMappingStrings())
            .Select(e => (e.SystemType, e.ApiNames))
            .ToList();

    /// <summary>설정 프리셋의 SystemType 이름만 정렬해서 반환.</summary>
    public static IReadOnlyList<string> GetSystemTypes() =>
        GetEntries()
            .Select(e => e.SystemType)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// 특정 SystemType 의 API 이름 목록. 정확 일치 우선, 없으면 'Cylinder_#' 같은 템플릿 entry 매칭.
    /// 결정 로직은 F# <see cref="SystemTypePreset.lookupApiNames"/>.
    /// </summary>
    public static string[] GetApiNames(string systemType) =>
        SystemTypePreset.lookupApiNames(
            SystemTypePreset.parseEntries(LoadMappingStrings()),
            systemType);

    // ── 내부 로더 (file IO + JSON 역직렬화는 C# 책임) ─────────────────────

    private static string[] LoadMappingStrings()
    {
        try
        {
            if (File.Exists(PresetFilePath))
            {
                var json = File.ReadAllText(PresetFilePath);
                var parsed = JsonSerializer.Deserialize<string[]>(json);
                if (parsed != null && parsed.Length > 0)
                    return SystemTypePreset.mergeWithDefaults(parsed, BuildDefaultMappingStrings()).ToArray();
            }
        }
        catch { /* fall through */ }

        return BuildDefaultMappingStrings();
    }

    /// <summary>
    /// "ApiList:SystemType" 기본 매핑 문자열 배열 — F# 위임.
    /// 외부(테스트 등)에서 직접 호출 가능하도록 public.
    /// </summary>
    public static string[] BuildDefaultMappingStrings() =>
        SystemTypePreset.buildDefaultMappingStrings().ToArray();
}
