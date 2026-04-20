using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Ds2.Core;
using Ds2.Core.Store;
using Microsoft.FSharp.Core;

namespace Promaker.Services;

/// <summary>
/// 디바이스 타입(SystemType)별 FBTagMap 프리셋 저장소.
/// 프리셋은 첫 번째 ActiveSystem의 ControlSystemProperties에 저장 → AASX에 포함된다.
/// AppData JSON은 하위 호환 폴백으로만 사용 (읽기 전용).
/// </summary>
public static class FBTagMapStore
{
    // ── AppData 폴백 경로 (구버전 호환) ─────────────────────────────────────
    private static readonly string LegacyStoreFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Dualsoft", "Promaker", "fbTagMaps.json");

    // ── 첫 번째 ActiveSystem의 ControlSystemProperties 접근 ─────────────────

    private static ControlSystemProperties? GetOrCreateControlProps(DsStore store)
    {
        var projects = Queries.allProjects(store);
        if (projects.IsEmpty) return null;
        var activeSystems = Queries.activeSystemsOf(projects.Head.Id, store);
        if (activeSystems.IsEmpty) return null;

        var sys = activeSystems.Head;
        var ctrlOpt = sys.GetControlProperties();
        if (!FSharpOption<ControlSystemProperties>.get_IsSome(ctrlOpt))
        {
            var cp = new ControlSystemProperties();
            sys.SetControlProperties(cp);
            return cp;
        }
        return ctrlOpt.Value;
    }

    // ── Store 기반 접근 ─────────────────────────────────────────────────────

    /// <summary>
    /// 첫 번째 ActiveSystem의 FBTagMapPresets를 DTO 딕셔너리로 반환.
    /// 프리셋이 비어있으면 AppData JSON에서 폴백 로드 (구버전 마이그레이션).
    /// </summary>
    public static Dictionary<string, FBTagMapPresetDto> LoadAll(DsStore store)
    {
        var cp = GetOrCreateControlProps(store);
        if (cp != null && cp.FBTagMapPresets.Count > 0)
            return ToDtoDict(cp.FBTagMapPresets);

        return LoadLegacy();
    }

    /// <summary>프리셋 저장 — 첫 번째 ActiveSystem의 ControlSystemProperties에 직접 기록.</summary>
    public static void Save(DsStore store, string deviceType, FBTagMapPresetDto preset)
    {
        if (string.IsNullOrWhiteSpace(deviceType)) return;
        var cp = GetOrCreateControlProps(store);
        if (cp == null) return;

        var corePreset = new FBTagMapPreset();
        corePreset.FBTagMapName = preset.FBTagMapName ?? "";
        foreach (var p in preset.Ports ?? new List<FBTagMapPortDto>())
        {
            var port = new FBTagMapPort();
            port.FBPort     = p.FBPort     ?? "";
            port.Direction  = p.Direction  ?? "";
            port.DataType   = p.DataType   ?? "BOOL";
            port.TagPattern = p.TagPattern ?? "";
            port.IsDummy    = p.IsDummy;
            corePreset.FBTagMapTemplate.Add(port);
        }
        cp.FBTagMapPresets[deviceType] = corePreset;
    }

    /// <summary>프리셋 삭제 — 첫 번째 ActiveSystem의 ControlSystemProperties에서 제거.</summary>
    public static void Remove(DsStore store, string deviceType)
    {
        var cp = GetOrCreateControlProps(store);
        cp?.FBTagMapPresets.Remove(deviceType);
    }

    /// <summary>
    /// 첫 번째 ActiveSystem의 FBTagMapPresets를 AAStoXGI가 소비하는
    /// FSharpMap&lt;SystemType, FBTagMapPreset&gt; 형태로 변환.
    /// 프리셋이 비어있으면 AppData JSON 폴백 사용.
    /// </summary>
    public static Microsoft.FSharp.Collections.FSharpMap<string, FBTagMapPreset> ToFSharpMap(DsStore store)
    {
        var cp = GetOrCreateControlProps(store);
        var source = (cp != null && cp.FBTagMapPresets.Count > 0)
            ? cp.FBTagMapPresets
            : ToCoreDictFromDto(LoadLegacy());

        var pairs = source.Select(kv =>
            new Tuple<string, FBTagMapPreset>(kv.Key, kv.Value));
        return Microsoft.FSharp.Collections.MapModule.OfSeq(pairs);
    }

    // ── AppData JSON 마이그레이션 (폴백, 읽기 전용) ──────────────────────────

    /// <summary>AppData JSON에서 프리셋 로드 (레거시 폴백).</summary>
    public static Dictionary<string, FBTagMapPresetDto> LoadLegacy()
    {
        try
        {
            if (!File.Exists(LegacyStoreFile))
                return new Dictionary<string, FBTagMapPresetDto>();

            var json = File.ReadAllText(LegacyStoreFile);
            return JsonSerializer.Deserialize<Dictionary<string, FBTagMapPresetDto>>(json)
                   ?? new Dictionary<string, FBTagMapPresetDto>();
        }
        catch
        {
            return new Dictionary<string, FBTagMapPresetDto>();
        }
    }

    /// <summary>
    /// AppData JSON 프리셋을 첫 번째 ActiveSystem의 ControlSystemProperties에 마이그레이션.
    /// 기존 프리셋이 없는 경우에만 수행.
    /// </summary>
    public static void MigrateFromLegacyIfEmpty(DsStore store)
    {
        var cp = GetOrCreateControlProps(store);
        if (cp == null || cp.FBTagMapPresets.Count > 0) return;

        var legacy = LoadLegacy();
        foreach (var kv in ToCoreDictFromDto(legacy))
            cp.FBTagMapPresets[kv.Key] = kv.Value;
    }

    // ── 내부 변환 헬퍼 ──────────────────────────────────────────────────────

    private static Dictionary<string, FBTagMapPresetDto> ToDtoDict(
        Dictionary<string, FBTagMapPreset> source)
    {
        var result = new Dictionary<string, FBTagMapPresetDto>();
        foreach (var kv in source)
        {
            var dto = new FBTagMapPresetDto { FBTagMapName = kv.Value.FBTagMapName };
            foreach (var p in kv.Value.FBTagMapTemplate)
                dto.Ports.Add(new FBTagMapPortDto
                {
                    FBPort     = p.FBPort,
                    Direction  = p.Direction,
                    DataType   = p.DataType,
                    TagPattern = p.TagPattern,
                    IsDummy    = p.IsDummy,
                });
            result[kv.Key] = dto;
        }
        return result;
    }

    private static Dictionary<string, FBTagMapPreset> ToCoreDictFromDto(
        Dictionary<string, FBTagMapPresetDto> source)
    {
        var result = new Dictionary<string, FBTagMapPreset>();
        foreach (var kv in source)
        {
            var preset = new FBTagMapPreset { FBTagMapName = kv.Value.FBTagMapName ?? "" };
            foreach (var p in kv.Value.Ports ?? new List<FBTagMapPortDto>())
                preset.FBTagMapTemplate.Add(new FBTagMapPort
                {
                    FBPort     = p.FBPort     ?? "",
                    Direction  = p.Direction  ?? "",
                    DataType   = p.DataType   ?? "BOOL",
                    TagPattern = p.TagPattern ?? "",
                    IsDummy    = p.IsDummy,
                });
            result[kv.Key] = preset;
        }
        return result;
    }
}

/// <summary>JSON 직렬화용 FBTagMapPreset DTO</summary>
public class FBTagMapPresetDto
{
    public string FBTagMapName { get; set; } = "";
    public List<FBTagMapPortDto> Ports { get; set; } = new();
}

/// <summary>JSON 직렬화용 FBTagMapPort DTO</summary>
public class FBTagMapPortDto
{
    public string FBPort     { get; set; } = "";
    public string Direction  { get; set; } = "Input";
    public string DataType   { get; set; } = "BOOL";
    public string TagPattern { get; set; } = "";
    public bool   IsDummy    { get; set; } = false;
}
