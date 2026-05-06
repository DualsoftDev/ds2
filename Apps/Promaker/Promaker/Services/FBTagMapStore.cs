using System;
using System.Collections.Generic;
using System.Linq;
using Ds2.Core;
using Ds2.Core.Store;
using Microsoft.FSharp.Core;

namespace Promaker.Services;

/// <summary>
/// 디바이스 타입(SystemType)별 FBTagMap 프리셋 저장소.
///
/// 단일 진실원: AAStoPLC.dll 임베디드 JSON (Resources/FBTagMapDefaults/*.json).
/// 매번 LoadAll/LoadOne 호출 시 JSON 으로부터 preset 을 통째로 재생성한다.
/// 사용자 편집 (Save 호출) 은 store 에 반영되지만, 다음 LoadAll 에서 JSON 으로 다시 덮어써짐.
/// 따라서 사용자가 패턴을 영구 변경하려면 JSON 자체를 수정해야 함.
/// </summary>
public static class FBTagMapStore
{
    private static ControlSystemProperties? GetOrCreateCp(DsStore store)
    {
        var opt = Queries.getOrCreatePrimaryControlProps(store);
        return FSharpOption<ControlSystemProperties>.get_IsSome(opt) ? opt.Value : null;
    }

    public static Dictionary<string, FBTagMapPresetDto> LoadAll(DsStore store)
    {
        var cp = GetOrCreateCp(store);
        if (cp == null) return new();
        RebuildPresetsFromJson(cp);
        return ToDtoDict(cp.FBTagMapPresets);
    }

    /// <summary>특정 SystemType 프리셋 1개만 DTO 로 변환.</summary>
    public static FBTagMapPresetDto? LoadOne(DsStore store, string systemType)
    {
        if (string.IsNullOrWhiteSpace(systemType)) return null;
        var cp = GetOrCreateCp(store);
        if (cp == null) return null;
        RebuildPresetsFromJson(cp);
        return cp.FBTagMapPresets.TryGetValue(systemType, out var preset) ? PresetToDto(preset) : null;
    }

    /// <summary>
    /// 모든 SystemType (DevicePresets.Entries3) 의 preset 을 JSON 기준으로 재생성.
    /// 기존 entry 는 모두 폐기. 사용자 추가분 보존 안 함.
    /// </summary>
    public static void RebuildPresetsFromJson(ControlSystemProperties cp)
    {
        foreach (var (sysType, _, defaultFb) in DevicePresets.Entries3)
        {
            if (string.IsNullOrWhiteSpace(sysType)) continue;
            cp.FBTagMapPresets[sysType] = BuildPresetFromJson(sysType, defaultFb);
        }
    }

    /// <summary>임베디드 JSON → FBTagMapPreset 통째 변환. JSON 없으면 FBTagMapName 만 채운 빈 preset.</summary>
    private static FBTagMapPreset BuildPresetFromJson(string sysType, string? defaultFb)
    {
        var dto = FBTagMapEmbeddedDefaults.Load(sysType);
        var preset = new FBTagMapPreset
        {
            FBTagMapName      = !string.IsNullOrEmpty(dto?.FBTagMapName) ? dto!.FBTagMapName
                                : (defaultFb ?? ""),
            AddressRule       = dto != null ? ParseRule(dto.AddressRule) : AddressAssignRule.Sequential,
            IsOperationModeFb = dto?.IsOperationModeFb ?? false,
        };
        if (dto?.BaseAddresses != null)
        {
            preset.BaseAddresses.InputBase  = dto.BaseAddresses.InputBase  ?? preset.BaseAddresses.InputBase;
            preset.BaseAddresses.OutputBase = dto.BaseAddresses.OutputBase ?? preset.BaseAddresses.OutputBase;
            preset.BaseAddresses.MemoryBase = dto.BaseAddresses.MemoryBase ?? preset.BaseAddresses.MemoryBase;
        }
        if (dto != null)
        {
            CopyDict(dto.AutoAuxPortMap, preset.AutoAuxPortMap);
            CopyDict(dto.ComAuxPortMap,  preset.ComAuxPortMap);
            CopyList(dto.IwPatterns, preset.IwPatterns, FromDtoSig);
            CopyList(dto.QwPatterns, preset.QwPatterns, FromDtoSig);
            CopyList(dto.MwPatterns, preset.MwPatterns, FromDtoSig);
            foreach (var p in dto.SkippedFBPorts ?? new())
                if (!string.IsNullOrEmpty(p))
                    preset.SkippedFBPorts.Add(p);
        }
        return preset;
    }

    public static void Save(DsStore store, string deviceType, FBTagMapPresetDto preset)
    {
        if (string.IsNullOrWhiteSpace(deviceType)) return;
        var cp = GetOrCreateCp(store);
        if (cp == null) return;

        var core = new FBTagMapPreset
        {
            FBTagMapName      = preset.FBTagMapName ?? "",
            AddressRule       = ParseRule(preset.AddressRule),
            IsOperationModeFb = preset.IsOperationModeFb,
        };
        if (preset.BaseAddresses != null)
        {
            core.BaseAddresses.InputBase  = preset.BaseAddresses.InputBase  ?? core.BaseAddresses.InputBase;
            core.BaseAddresses.OutputBase = preset.BaseAddresses.OutputBase ?? core.BaseAddresses.OutputBase;
            core.BaseAddresses.MemoryBase = preset.BaseAddresses.MemoryBase ?? core.BaseAddresses.MemoryBase;
        }
        CopyDict(preset.AutoAuxPortMap, core.AutoAuxPortMap);
        CopyDict(preset.ComAuxPortMap,  core.ComAuxPortMap);
        CopyList(preset.IwPatterns, core.IwPatterns, FromDtoSig);
        CopyList(preset.QwPatterns, core.QwPatterns, FromDtoSig);
        CopyList(preset.MwPatterns, core.MwPatterns, FromDtoSig);
        if (preset.SkippedFBPorts != null)
            foreach (var p in preset.SkippedFBPorts) core.SkippedFBPorts.Add(p);
        cp.FBTagMapPresets[deviceType] = core;
    }

    public static void Remove(DsStore store, string deviceType)
    {
        var cp = GetOrCreateCp(store);
        cp?.FBTagMapPresets.Remove(deviceType);
    }

    public static Microsoft.FSharp.Collections.FSharpMap<string, FBTagMapPreset> ToFSharpMap(DsStore store)
    {
        var cp = GetOrCreateCp(store);
        var source = cp?.FBTagMapPresets ?? new Dictionary<string, FBTagMapPreset>();
        var pairs = source.Select(kv => new Tuple<string, FBTagMapPreset>(kv.Key, kv.Value));
        return Microsoft.FSharp.Collections.MapModule.OfSeq(pairs);
    }

    // ── DTO ↔ core 변환 ──────────────────────────────────────────────────────

    private static FBTagMapPresetDto PresetToDto(FBTagMapPreset src)
    {
        var dto = new FBTagMapPresetDto
        {
            FBTagMapName      = src.FBTagMapName ?? "",
            AddressRule       = src.AddressRule.ToString(),
            BaseAddresses     = new BaseAddressDto
            {
                InputBase  = src.BaseAddresses.InputBase,
                OutputBase = src.BaseAddresses.OutputBase,
                MemoryBase = src.BaseAddresses.MemoryBase,
            },
            IsOperationModeFb = src.IsOperationModeFb,
        };
        CopyDict(src.AutoAuxPortMap, dto.AutoAuxPortMap);
        CopyDict(src.ComAuxPortMap,  dto.ComAuxPortMap);
        CopyList(src.IwPatterns, dto.IwPatterns, ToDtoSig);
        CopyList(src.QwPatterns, dto.QwPatterns, ToDtoSig);
        CopyList(src.MwPatterns, dto.MwPatterns, ToDtoSig);
        if (src.SkippedFBPorts != null)
            foreach (var p in src.SkippedFBPorts) dto.SkippedFBPorts.Add(p);
        return dto;
    }

    private static Dictionary<string, FBTagMapPresetDto> ToDtoDict(Dictionary<string, FBTagMapPreset> source)
    {
        var result = new Dictionary<string, FBTagMapPresetDto>();
        foreach (var kv in source)
            result[kv.Key] = PresetToDto(kv.Value);
        return result;
    }

    private static SignalPatternEntryDto ToDtoSig(SignalPatternEntry e) => new()
    {
        ApiName          = e.ApiName ?? "",
        Pattern          = e.Pattern ?? "",
        TargetFBPort     = e.TargetFBPort ?? "",
        SkipAddressAlloc = e.SkipAddressAlloc,
        IsSpare          = e.IsSpare,
    };

    private static SignalPatternEntry FromDtoSig(SignalPatternEntryDto d) => new()
    {
        ApiName          = d.ApiName ?? "",
        Pattern          = d.Pattern ?? "",
        TargetFBPort     = d.TargetFBPort ?? "",
        SkipAddressAlloc = d.SkipAddressAlloc,
        IsSpare          = d.IsSpare,
    };

    private static AddressAssignRule ParseRule(string? s) =>
        s switch
        {
            "PortIndex" => AddressAssignRule.PortIndex,
            "Manual"    => AddressAssignRule.Manual,
            _           => AddressAssignRule.Sequential,
        };

    private static void CopyDict<TV>(IDictionary<string, TV>? src, IDictionary<string, TV> dst)
    {
        if (src == null) return;
        foreach (var kv in src) dst[kv.Key] = kv.Value;
    }

    private static void CopyList<TSrc, TDst>(IEnumerable<TSrc>? src, ICollection<TDst> dst, Func<TSrc, TDst> map)
    {
        if (src == null) return;
        foreach (var s in src) dst.Add(map(s));
    }
}

public class FBTagMapPresetDto
{
    public string FBTagMapName { get; set; } = "";
    public string AddressRule { get; set; } = "Sequential";
    public BaseAddressDto? BaseAddresses { get; set; } = new();
    public Dictionary<string, string> AutoAuxPortMap { get; set; } = new();
    public Dictionary<string, string> ComAuxPortMap { get; set; } = new();
    public List<SignalPatternEntryDto> IwPatterns { get; set; } = new();
    public List<SignalPatternEntryDto> QwPatterns { get; set; } = new();
    public List<SignalPatternEntryDto> MwPatterns { get; set; } = new();
    /// <summary>Active System Operation Mode FB 여부 (예: ModeStn) — true 이면 Api_None Call-only 모드 지원.</summary>
    public bool IsOperationModeFb { get; set; } = false;
    /// <summary>FB 호출 시 의도적으로 미연결로 남길 FB 포트 이름 목록 (예: "로보트기동이상").</summary>
    public List<string> SkippedFBPorts { get; set; } = new();
}

public class SignalPatternEntryDto
{
    public string ApiName         { get; set; } = "";
    public string Pattern         { get; set; } = "";
    public string TargetFBPort    { get; set; } = "";
    /// <summary>주소 할당 미진행 — true 면 IO 슬롯 미소비, Address 빈값 emit. 예: _T1S, T#200MS.</summary>
    public bool SkipAddressAlloc  { get; set; } = false;
    /// <summary>예비(Spare) 슬롯 — true 면 주소 1비트 예약, 신호 미생성. 기타 필드 무시.</summary>
    public bool IsSpare           { get; set; } = false;
}

public class BaseAddressDto
{
    public string InputBase  { get; set; } = "%IW0.0.0";
    public string OutputBase { get; set; } = "%QW0.0.0";
    public string MemoryBase { get; set; } = "%MW100";
}
