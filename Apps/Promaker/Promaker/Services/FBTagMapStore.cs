using System;
using System.Collections.Generic;
using System.Linq;
using Ds2.Core;
using Ds2.Core.Store;
using Microsoft.FSharp.Core;

namespace Promaker.Services;

/// <summary>
/// 디바이스 타입(SystemType)별 FBTagMap 프리셋 저장소.
/// 첫 번째 ActiveSystem 의 ControlSystemProperties 에 저장 → AASX 에 포함된다.
/// </summary>
public static class FBTagMapStore
{
    private static ControlSystemProperties? GetOrCreateCp(DsStore store)
    {
        var opt = Queries.getOrCreatePrimaryControlProps(store);
        return Microsoft.FSharp.Core.FSharpOption<ControlSystemProperties>.get_IsSome(opt) ? opt.Value : null;
    }

    public static Dictionary<string, FBTagMapPresetDto> LoadAll(DsStore store)
    {
        var cp = GetOrCreateCp(store);
        if (cp == null) return new();
        EnsureDefaultPresets(cp);
        return ToDtoDict(cp.FBTagMapPresets);
    }

    /// <summary>특정 SystemType 프리셋 1개만 DTO 로 변환 (LoadAll 의 부분 변형 — 빈번 호출용).</summary>
    public static FBTagMapPresetDto? LoadOne(DsStore store, string systemType)
    {
        if (string.IsNullOrWhiteSpace(systemType)) return null;
        var cp = GetOrCreateCp(store);
        if (cp == null) return null;
        EnsureDefaultPresets(cp);
        return cp.FBTagMapPresets.TryGetValue(systemType, out var preset) ? PresetToDto(preset) : null;
    }

    /// <summary>
    /// 등록된 SystemType 별 FBTagMapPreset 을 보강한다.
    /// • Entry 가 없으면 신규 생성 + 기본 FB + 기본 패턴 채움.
    /// • Entry 가 있어도 누락된 기본값이 있으면 보충 (사용자 편집은 보존).
    /// 디폴트 출처: AAStoPLC.dll 임베디드 JSON — 디스크 파일 사용 안 함.
    /// </summary>
    public static void EnsureDefaultPresets(ControlSystemProperties cp)
    {
        // 모든 SystemType 의 default FB 집합 — 잘못된 SystemType↔FB 교차 오염 감지용.
        var allDefaultFbs = Ds2.Core.Store.DevicePresets.Entries3
            .Select(t => t.Item3)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (sysType, _, defaultFb) in Ds2.Core.Store.DevicePresets.Entries3)
        {
            if (string.IsNullOrWhiteSpace(sysType)) continue;

            FBTagMapPreset preset;
            if (!cp.FBTagMapPresets.TryGetValue(sysType, out preset!))
            {
                preset = new FBTagMapPreset();
                cp.FBTagMapPresets[sysType] = preset;
            }

            // FBTagMapName 검증 / 복원:
            //   • 비어있음 → defaultFb 로 채움
            //   • 다른 SystemType 의 default FB 와 매칭 → 이전 버그로 인한 교차 오염 → defaultFb 로 reset
            //   • 그 외 (현재 default 와 일치 / 사용자 커스텀 FB) → 보존
            var current = preset.FBTagMapName ?? "";
            var expected = defaultFb ?? "";
            if (string.IsNullOrEmpty(current))
            {
                preset.FBTagMapName = expected;
            }
            else if (!string.Equals(current, expected, StringComparison.OrdinalIgnoreCase)
                  && allDefaultFbs.Contains(current))
            {
                // 다른 SystemType 의 default 가 박혀있음 → 잘못된 매핑으로 판정 후 reset.
                preset.FBTagMapName = expected;
            }

            MergeDefaultsIntoPreset(preset, BuildFactoryDto(sysType, defaultFb));
        }
    }

    /// <summary>임베디드 JSON 디폴트 DTO. 부재 시 FBTagMapName 만 채운 빈 DTO.</summary>
    private static FBTagMapPresetDto BuildFactoryDto(string sysType, string? defaultFb)
    {
        var dto = FBTagMapEmbeddedDefaults.Load(sysType) ?? new FBTagMapPresetDto();
        if (string.IsNullOrWhiteSpace(dto.FBTagMapName) && !string.IsNullOrEmpty(defaultFb))
            dto.FBTagMapName = defaultFb!;
        return dto;
    }

    /// <summary>
    /// DTO 와 preset 동기화. 임베디드 JSON 순서를 단일 진실원으로 사용.
    /// 매칭되는 preset entry 는 보존 (사용자 편집 유지), 임베디드에 없는 사용자 추가분은 끝에.
    /// </summary>
    private static void MergeDefaultsIntoPreset(FBTagMapPreset preset, FBTagMapPresetDto dto)
    {
        if (string.IsNullOrEmpty(preset.FBTagMapName) && !string.IsNullOrEmpty(dto.FBTagMapName))
            preset.FBTagMapName = dto.FBTagMapName;

        SyncSectionOrdered(preset.IwPatterns, dto.IwPatterns);
        SyncSectionOrdered(preset.QwPatterns, dto.QwPatterns);
        SyncSectionOrdered(preset.MwPatterns, dto.MwPatterns);
        foreach (var kv in dto.AutoAuxPortMap ?? new()) EnsureAux(preset.AutoAuxPortMap, kv.Key, kv.Value);
        foreach (var kv in dto.ComAuxPortMap ?? new()) EnsureAux(preset.ComAuxPortMap, kv.Key, kv.Value);
        if (dto.IsOperationModeFb) preset.IsOperationModeFb = true;
        if (dto.SkippedFBPorts != null)
        {
            preset.SkippedFBPorts ??= new System.Collections.Generic.List<string>();
            var existing = new HashSet<string>(preset.SkippedFBPorts, StringComparer.OrdinalIgnoreCase);
            foreach (var p in dto.SkippedFBPorts)
                if (!string.IsNullOrEmpty(p) && existing.Add(p))
                    preset.SkippedFBPorts.Add(p);
        }
    }

    /// <summary>
    /// preset 섹션을 임베디드 dto 순서대로 재구성.
    ///   • dto 의 각 entry 에 대해 — preset 에 동일 (Api, Pattern, FBPort) 가 있으면 그 인스턴스 보존, 없으면 새로 만듦.
    ///   • 임베디드에 없는 preset entry (사용자 추가분) 는 끝에 append.
    ///     단, "Api_None"/"-" 은 SystemType 고유 글로벌/슬롯이므로 leftover 보존 대상에서 제외
    ///     (다른 SystemType 에서 오염된 데이터 자동 제거).
    /// </summary>
    private static void SyncSectionOrdered(
        System.Collections.Generic.ICollection<SignalPatternEntry> presetSection,
        System.Collections.Generic.List<SignalPatternEntryDto>? dtoSection)
    {
        // Api_None / Spare / 레거시 "-" 는 dto 가 단일 진실원 — preset 보존하지 않고 dto 로 재생성.
        static bool IsSystemTypeSpecific(SignalPatternEntry e) =>
            e.IsSpare
            || string.Equals(e.ApiName ?? "", "Api_None", StringComparison.OrdinalIgnoreCase)
            || string.Equals(e.ApiName ?? "", "-", StringComparison.Ordinal);
        static bool IsSystemTypeSpecificDto(SignalPatternEntryDto d) =>
            d.IsSpare
            || string.Equals(d.ApiName ?? "", "Api_None", StringComparison.OrdinalIgnoreCase)
            || string.Equals(d.ApiName ?? "", "-", StringComparison.Ordinal);

        // dto 가 비어있으면 — Api_None/Spare 오염 entry 만 제거하고 일반 사용자 entry 는 보존.
        if (dtoSection == null || dtoSection.Count == 0)
        {
            var keep = presetSection
                .Where(e => !IsSystemTypeSpecific(e))
                .ToList();
            presetSection.Clear();
            foreach (var e in keep) presetSection.Add(e);
            return;
        }

        var available = new System.Collections.Generic.Dictionary<(string, string, string), System.Collections.Generic.Queue<SignalPatternEntry>>();
        foreach (var p in presetSection)
        {
            if (IsSystemTypeSpecific(p)) continue;
            var key = (p.ApiName ?? "", p.Pattern ?? "", p.TargetFBPort ?? "");
            if (!available.TryGetValue(key, out var q)) { q = new(); available[key] = q; }
            q.Enqueue(p);
        }

        var newOrder = new System.Collections.Generic.List<SignalPatternEntry>(dtoSection.Count);
        foreach (var d in dtoSection)
        {
            // 레거시 ApiName="-" 항목 → IsSpare 로 마이그레이션.
            var isSpareEntry = d.IsSpare || string.Equals(d.ApiName ?? "", "-", StringComparison.Ordinal);
            if (isSpareEntry)
            {
                var sp = MakeSig("", "", "", false);
                sp.IsSpare = true;
                newOrder.Add(sp);
                continue;
            }
            var api = d.ApiName ?? "";
            if (IsSystemTypeSpecificDto(d))
            {
                newOrder.Add(MakeSig(api, d.Pattern ?? "", d.TargetFBPort ?? "", d.SkipAddressAlloc));
                continue;
            }
            var key = (api, d.Pattern ?? "", d.TargetFBPort ?? "");
            if (available.TryGetValue(key, out var q) && q.Count > 0)
                newOrder.Add(q.Dequeue());
            else
                newOrder.Add(MakeSig(api, d.Pattern ?? "", d.TargetFBPort ?? "", d.SkipAddressAlloc));
        }

        // 사용자 추가분 (dto 에 없는 비-글로벌 entry) — 끝에 보존.
        foreach (var kv in available)
            foreach (var leftover in kv.Value)
                newOrder.Add(leftover);

        presetSection.Clear();
        foreach (var e in newOrder) presetSection.Add(e);
    }

    /// <summary>FBTagMapPreset → DTO. legacy FBTagMapTemplate (Ports) 은 직렬화하지 않음.</summary>
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

    /// <summary>AUX 맵: 기존 키가 비어있을 때만 default 채움.</summary>
    private static void EnsureAux(System.Collections.Generic.Dictionary<string, string> map, string api, string defaultPort)
    {
        if (!map.ContainsKey(api) || string.IsNullOrEmpty(map[api]))
            map[api] = defaultPort;
    }

    private static SignalPatternEntry MakeSig(string apiName, string pattern, string targetFbPort, bool skipAddressAlloc = false) =>
        new() { ApiName = apiName, Pattern = pattern, TargetFBPort = targetFbPort, SkipAddressAlloc = skipAddressAlloc };

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

    private static Dictionary<string, FBTagMapPresetDto> ToDtoDict(
        Dictionary<string, FBTagMapPreset> source)
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

