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

    /// <summary>
    /// 등록된 SystemType 별 FBTagMapPreset 을 보강한다.
    /// • Entry 가 없으면 신규 생성 + 기본 FB + 기본 패턴 채움.
    /// • Entry 가 있어도 누락된 기본값이 있으면 보충 (사용자 편집은 보존).
    /// 디폴트 값의 출처: <c>%APPDATA%\Dualsoft\Promaker\FBTagMap\&lt;SystemType&gt;.json</c>
    /// (첫 실행 시 factory 디폴트로 시드 → 이후 JSON 파일이 truth source)
    /// </summary>
    public static void EnsureDefaultPresets(ControlSystemProperties cp)
    {
        var entries = Ds2.Core.Store.DevicePresets.Entries3
            .Select(t => (sysType: t.Item1, defaultFb: (string?)t.Item3));
        FBTagMapDefaultsRepository.EnsureSeeded(BuildFactoryDto, entries);

        foreach (var (sysType, _, defaultFb) in Ds2.Core.Store.DevicePresets.Entries3)
        {
            if (string.IsNullOrWhiteSpace(sysType)) continue;

            FBTagMapPreset preset;
            if (!cp.FBTagMapPresets.TryGetValue(sysType, out preset!))
            {
                preset = new FBTagMapPreset();
                cp.FBTagMapPresets[sysType] = preset;
            }
            if (string.IsNullOrEmpty(preset.FBTagMapName))
                preset.FBTagMapName = defaultFb ?? "";

            // 1순위: AppData JSON / 2순위: hardcoded factory fallback
            var dto = FBTagMapDefaultsRepository.Load(sysType) ?? BuildFactoryDto(sysType, defaultFb);
            MergeDefaultsIntoPreset(preset, dto);
        }
    }

    /// <summary>현재 SystemType 디폴트를 factory 로 생성하여 JSON 으로 export (사용자 "디폴트 리셋" 명령용).</summary>
    public static void ResetDefaultsToFactory()
    {
        var entries = Ds2.Core.Store.DevicePresets.Entries3
            .Select(t => (sysType: t.Item1, defaultFb: (string?)t.Item3));
        FBTagMapDefaultsRepository.ResetAll(BuildFactoryDto, entries);
    }

    /// <summary>코드의 factory 디폴트로부터 DTO 생성 (시드/fallback 용).</summary>
    private static FBTagMapPresetDto BuildFactoryDto(string sysType, string? defaultFb)
    {
        var preset = new FBTagMapPreset();
        preset.FBTagMapName = defaultFb ?? "";
        ApplyBuiltInDefaults(preset, sysType);
        return PresetToDto(preset);
    }

    /// <summary>DTO 의 디폴트 값을 preset 에 머지 — 같은 (Api, FBPort) / 같은 AUX 키가 이미 있으면 skip.</summary>
    private static void MergeDefaultsIntoPreset(FBTagMapPreset preset, FBTagMapPresetDto dto)
    {
        if (string.IsNullOrEmpty(preset.FBTagMapName) && !string.IsNullOrEmpty(dto.FBTagMapName))
            preset.FBTagMapName = dto.FBTagMapName;

        foreach (var e in dto.IwPatterns ?? new()) EnsureIwPattern(preset, e.ApiName, e.Pattern, e.TargetFBPort);
        foreach (var e in dto.QwPatterns ?? new()) EnsureQwPattern(preset, e.ApiName, e.Pattern, e.TargetFBPort);
        foreach (var e in dto.MwPatterns ?? new()) EnsureMwPattern(preset, e.ApiName, e.Pattern, e.TargetFBPort);
        foreach (var kv in dto.AutoAuxPortMap ?? new()) EnsureAux(preset.AutoAuxPortMap, kv.Key, kv.Value);
        foreach (var kv in dto.ComAuxPortMap ?? new()) EnsureAux(preset.ComAuxPortMap, kv.Key, kv.Value);
    }

    /// <summary>SystemType 별 기본 신호 패턴 / AUX 포트 매핑을 보충 (코드 factory).</summary>
    private static void ApplyBuiltInDefaults(FBTagMapPreset preset, string sysType)
    {
        var sizeOpt = Ds2.Core.Store.DevicePresets.tryCylinderSize(sysType);
        if (Microsoft.FSharp.Core.FSharpOption<int>.get_IsSome(sizeOpt))
        {
            ApplyCylinderDefaults(preset, sizeOpt.Value);
            return;
        }
        switch (sysType)
        {
            case "RobotWeldGrip":       ApplyRobotDefaults(preset, includePallet: false); break;
            case "RobotWeldGripPallet": ApplyRobotDefaults(preset, includePallet: true);  break;
        }
    }

    /// <summary>FBTagMapPreset → DTO (단일 변환 — 시드 파일 작성용).</summary>
    private static FBTagMapPresetDto PresetToDto(FBTagMapPreset src)
    {
        var dto = new FBTagMapPresetDto
        {
            FBTagMapName = src.FBTagMapName ?? "",
            AddressRule  = src.AddressRule.ToString(),
            BaseAddresses = new BaseAddressDto
            {
                InputBase  = src.BaseAddresses.InputBase,
                OutputBase = src.BaseAddresses.OutputBase,
                MemoryBase = src.BaseAddresses.MemoryBase,
            },
        };
        foreach (var p in src.FBTagMapTemplate)
            dto.Ports.Add(new FBTagMapPortDto
            {
                FBPort     = p.FBPort,
                Direction  = p.Direction,
                DataType   = p.DataType,
                TagPattern = p.TagPattern,
                IsDummy    = p.IsDummy,
                AddressOffset = FSharpOption<int>.get_IsSome(p.AddressOffset)
                    ? p.AddressOffset.Value : null,
            });
        foreach (var akv in src.AutoAuxPortMap) dto.AutoAuxPortMap[akv.Key] = akv.Value;
        foreach (var ckv in src.ComAuxPortMap)  dto.ComAuxPortMap[ckv.Key]  = ckv.Value;
        foreach (var e in src.IwPatterns) dto.IwPatterns.Add(ToDtoSig(e));
        foreach (var e in src.QwPatterns) dto.QwPatterns.Add(ToDtoSig(e));
        foreach (var e in src.MwPatterns) dto.MwPatterns.Add(ToDtoSig(e));
        return dto;
    }

    /// <summary>
    /// RobotWeldGrip / RobotWeldGripPallet — MW 디폴트.
    /// 사용자 API 와 1:1 매칭되는 FB 포트는 해당 API 에, 나머지 글로벌 FB 포트는 모두
    /// "START" API 를 carrier 로 등록 (START 는 모든 로봇 Call 에 필수).
    /// </summary>
    private static void ApplyRobotDefaults(FBTagMapPreset preset, bool includePallet)
    {
        // 1) User API ↔ FB 포트 1:1 매핑 (per-API)
        var weldGripPerApi = new (string Api, string FbPort)[]
        {
            ("WORK_COMP_RST", "M_LAST_WK_COMP_RST"),
            ("START",         "M_START_OK"),
            ("A_1ST_IN_OK",   "M_A_In_Ok"),
            ("B_1ST_IN_OK",   "M_B_In_Ok"),
            ("2ND_IN_OK",     "M_2nd_In_Ok"),
            ("3RD_IN_OK",     "M_3rd_In_Ok"),
            ("4TH_IN_OK",     "M_4th_In_Ok"),
            ("5TH_IN_OK",     "M_5th_In_Ok"),
            ("6TH_IN_OK",     "M_6th_In_Ok"),
            ("7TH_IN_OK",     "M_7th_In_Ok"),
        };
        var palletPerApi = new (string Api, string FbPort)[]
        {
            ("PLT1_IN_OK",     "M_PLT1_In_Ok"),
            ("PLT2_IN_OK",     "M_PLT2_In_Ok"),
            ("PLT3_IN_OK",     "M_PLT3_In_Ok"),
            ("PLT4_IN_OK",     "M_PLT4_In_Ok"),
            ("PLT1_COUNT_RST", "M_PLT1_COUNT_RST"),
            ("PLT2_COUNT_RST", "M_PLT2_COUNT_RST"),
            ("PLT3_COUNT_RST", "M_PLT3_COUNT_RST"),
            ("PLT4_COUNT_RST", "M_PLT4_COUNT_RST"),
        };
        const string perApiPattern = "W_$(F)_M_$(D)_$(A)";
        foreach (var (api, fbPort) in weldGripPerApi)
            EnsureMwPattern(preset, api, perApiPattern, fbPort);
        if (includePallet)
            foreach (var (api, fbPort) in palletPerApi)
                EnsureMwPattern(preset, api, perApiPattern, fbPort);

        // 2) 글로벌 FB 포트 — START API 를 carrier 로 사용. 패턴 W_$(F)_M_$(D)_<port>
        const string carrier = "START";
        foreach (var fbPort in RobotGlobalInputPorts)
            EnsureMwPattern(preset, carrier, $"W_$(F)_M_$(D)_{StripPrefix(fbPort)}", fbPort);
        foreach (var fbPort in RobotOutputPorts)
            EnsureMwPattern(preset, carrier, $"W_$(F)_M_$(D)_{StripPrefix(fbPort)}", fbPort);
        foreach (var fbPort in RobotMutualPorts)
            EnsureMwPattern(preset, carrier, $"W_$(F)_M_$(D)_{StripPrefix(fbPort)}", fbPort);

        if (includePallet)
        {
            // PLT5..12 dummy 슬롯 (사용자가 site 에 따라 채움)
            for (var i = 5; i <= 12; i++)
            {
                EnsureMwPattern(preset, carrier, $"W_$(F)_M_$(D)_PLT{i}_In_Ok",     $"M_PLT{i}_In_Ok");
                EnsureMwPattern(preset, carrier, $"W_$(F)_M_$(D)_PLT{i}_COUNT_RST", $"M_PLT{i}_COUNT_RST");
            }
        }

        // AUX 포트 — 로봇은 사용자 명시 영역 (Wizard 에서 직접 선택). 자동 채움 안 함.
    }

    private static readonly string[] RobotGlobalInputPorts = new[]
    {
        // 모드/제어 입력
        "RBT_Prog_No", "X_Ready_On", "M_Auto", "T_START",
        "M_Prog_OK", "M_RESTART_OK", "M_Stn_Fault", "M_Error_Rst", "M_Cycle_Set",
        "M_ATD_OK", "M_TIP_CHANGE_START", "M_TIP_CHANGE_COMPL",
        "M_WATER_CUT_OFF", "M_BUZZ_STOP_AUX", "M_GATE_CLOSED", "M_EM_STOP", "M_TEST",
        // C~H In_Ok (사용자 API 외 dummy)
        "M_C_In_Ok", "M_D_In_Ok", "M_E_In_Ok", "M_F_In_Ok", "M_G_In_Ok", "M_H_In_Ok",
        // Comp Reset (1st~7th)
        "M_1st_Comp_Rst", "M_2nd_Comp_Rst", "M_3rd_Comp_Rst", "M_4th_Comp_Rst",
        "M_5th_Comp_Rst", "M_6th_Comp_Rst", "M_7th_Comp_Rst",
        // 시퀀스/차종 워드
        "M_TO_RB_CN", "M_TO_RB_CTYE",
    };

    private static readonly string[] RobotOutputPorts = new[]
    {
        "M_RBT_Ready", "M_RBT_Auto", "M_RBT_Fault", "M_RBT_Home", "M_RBT_EM_Stop",
        "M_RBT_PAUSE", "M_RBT_PROGRAM_ERR", "M_RBT_PLC_RUN_CHK_ERR",
        "M_RBT_SEALER_CHK_BYPASS", "M_RBT_TIP_CHK_BYPASS", "M_RBT_VISION_BYPASS", "M_RBT_CLEANNER_BYPASS",
        "M_Last_COMP", "M_1st_COMP", "M_2nd_COMP", "M_3rd_COMP",
        "M_4th_COMP", "M_5th_COMP", "M_6th_COMP", "M_7th_COMP",
        "M_NO_INTF1", "M_NO_INTF2", "M_NO_INTF3", "M_NO_INTF4",
        "M_NO_INTF5", "M_NO_INTF6", "M_NO_INTF7", "M_NO_INTF8",
        "M_NO_INTF9", "M_NO_INTF10", "M_NO_INTF11", "M_NO_INTF12",
        "M_WELD_PLT_COUNT",
    };

    private static readonly string[] RobotMutualPorts = new[]
    {
        "Mutual_Int1", "Mutual_Int2", "Mutual_Int3", "Mutual_Int4", "Mutual_Int5",
        "Mutual_Int6", "Mutual_Int7", "Mutual_Int8", "Mutual_Int9", "Mutual_Int10",
    };

    /// <summary>FB 포트 이름에서 흔한 prefix 제거 (M_/M_RBT_ → 깔끔한 패턴 suffix).</summary>
    private static string StripPrefix(string fbPort)
    {
        if (fbPort.StartsWith("M_RBT_", StringComparison.Ordinal)) return fbPort.Substring("M_RBT_".Length);
        if (fbPort.StartsWith("M_",     StringComparison.Ordinal)) return fbPort.Substring("M_".Length);
        return fbPort;
    }

    /// <summary>Cylinder_N — 누락된 IW/QW/MW 패턴 + AUX 포트 매핑 보충.</summary>
    private static void ApplyCylinderDefaults(FBTagMapPreset preset, int n)
    {
        // === IW: 실제 입력 배선 — 센서 N 쌍 (per cylinder) ===
        for (var i = 1; i <= n; i++)
        {
            EnsureIwPattern(preset, "ADV", $"W_$(F)_WRS_$(D)_$(A){i}", $"LS_Adv{i}");
            EnsureIwPattern(preset, "RET", $"W_$(F)_WRS_$(D)_$(A){i}", $"LS_Ret{i}");
        }

        // === QW: SOL 공유 (ADV/RET 2행만) ===
        EnsureQwPattern(preset, "ADV", "W_$(F)_SOL_$(D)_$(A)", "SOL_Adv");
        EnsureQwPattern(preset, "RET", "W_$(F)_SOL_$(D)_$(A)", "SOL_Ret");

        // === MW: 메모리 — 글로벌 Flow 신호 (실제 입력 아님, 내부 메모리) ===
        var globalsMw = new (string FbPort, string Pattern)[]
        {
            ("General_JIG",   "_ON"),
            ("M_Ready",       "W_$(F)_M_READY_ON"),
            ("M_Auto_Run",    "W_$(F)_M_AUTO_START"),
            ("M_Manual",      "W_$(F)_M_MANUAL_MODE"),
            ("M_Home_Ret",    "W_$(F)_M_HOME_RET_AUX"),
            ("PB_Error_Rst",  "W_$(F)_M_ERR_RESET"),
            ("HMI_Sel",       "W_$(F)_$(D)_M_HMI_SEL"),
            ("HMI_Adv",       "W_$(F)_M_HMI_JIG_ADV_PB"),
            ("HMI_Ret",       "W_$(F)_M_HMI_JIG_RET_PB"),
            ("M_FLICK",       "_T1S"),
        };
        foreach (var (port, pattern) in globalsMw)
        {
            EnsureMwPattern(preset, "ADV", pattern, port);
            EnsureMwPattern(preset, "RET", pattern, port);
        }
        // === MW: 출력 메모리 (인접 FB 참조용) ===
        EnsureMwPattern(preset, "ADV", "W_$(F)_M_$(D)_$(A)_END",       "M_Adv_End");
        EnsureMwPattern(preset, "RET", "W_$(F)_M_$(D)_$(A)_END",       "M_Ret_End");
        EnsureMwPattern(preset, "ADV", "W_$(F)_M_$(D)_$(A)_ERR",       "M_Adv_Err");
        EnsureMwPattern(preset, "RET", "W_$(F)_M_$(D)_$(A)_ERR",       "M_Ret_Err");
        EnsureMwPattern(preset, "ADV", "W_$(F)_M_$(D)_$(A)_LAMP",      "LP_Adv");
        EnsureMwPattern(preset, "RET", "W_$(F)_M_$(D)_$(A)_LAMP",      "LP_Ret");
        EnsureMwPattern(preset, "ADV", "W_$(F)_M_$(D)_$(A)_HMI_LAMP",  "Visu_Adv");
        EnsureMwPattern(preset, "RET", "W_$(F)_M_$(D)_$(A)_HMI_LAMP",  "Visu_Ret");

        // === AUX 포트 ===
        EnsureAux(preset.AutoAuxPortMap, "ADV", "M_Auto_Adv");
        EnsureAux(preset.AutoAuxPortMap, "RET", "M_Auto_Ret");
        EnsureAux(preset.ComAuxPortMap , "ADV", "M_Com_Adv");
        EnsureAux(preset.ComAuxPortMap , "RET", "M_Com_Ret");
    }

    private static void EnsureIwPattern(FBTagMapPreset preset, string api, string pattern, string fbPort)
    {
        // 같은 (Api, FBPort) 가 이미 있으면 skip; 없으면 추가.
        foreach (var p in preset.IwPatterns)
            if (string.Equals(p.ApiName, api, StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.TargetFBPort, fbPort, StringComparison.OrdinalIgnoreCase))
                return;
        preset.IwPatterns.Add(MakeSig(api, pattern, fbPort));
    }

    private static void EnsureQwPattern(FBTagMapPreset preset, string api, string pattern, string fbPort)
    {
        foreach (var p in preset.QwPatterns)
            if (string.Equals(p.ApiName, api, StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.TargetFBPort, fbPort, StringComparison.OrdinalIgnoreCase))
                return;
        preset.QwPatterns.Add(MakeSig(api, pattern, fbPort));
    }

    private static void EnsureMwPattern(FBTagMapPreset preset, string api, string pattern, string fbPort)
    {
        foreach (var p in preset.MwPatterns)
            if (string.Equals(p.ApiName, api, StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.TargetFBPort, fbPort, StringComparison.OrdinalIgnoreCase))
                return;
        preset.MwPatterns.Add(MakeSig(api, pattern, fbPort));
    }

    private static void EnsureAux(System.Collections.Generic.Dictionary<string, string> map, string api, string defaultPort)
    {
        if (!map.ContainsKey(api) || string.IsNullOrEmpty(map[api]))
            map[api] = defaultPort;
    }

    private static SignalPatternEntry MakeSig(string apiName, string pattern, string targetFbPort) =>
        new() { ApiName = apiName, Pattern = pattern, TargetFBPort = targetFbPort };

    public static void Save(DsStore store, string deviceType, FBTagMapPresetDto preset)
    {
        if (string.IsNullOrWhiteSpace(deviceType)) return;
        var cp = GetOrCreateCp(store);
        if (cp == null) return;

        var corePreset = new FBTagMapPreset();
        corePreset.FBTagMapName = preset.FBTagMapName ?? "";
        corePreset.AddressRule = ParseRule(preset.AddressRule);
        if (preset.BaseAddresses != null)
        {
            corePreset.BaseAddresses.InputBase  = preset.BaseAddresses.InputBase  ?? corePreset.BaseAddresses.InputBase;
            corePreset.BaseAddresses.OutputBase = preset.BaseAddresses.OutputBase ?? corePreset.BaseAddresses.OutputBase;
            corePreset.BaseAddresses.MemoryBase = preset.BaseAddresses.MemoryBase ?? corePreset.BaseAddresses.MemoryBase;
        }
        foreach (var p in preset.Ports ?? new List<FBTagMapPortDto>())
        {
            var port = new FBTagMapPort();
            port.FBPort     = p.FBPort     ?? "";
            port.Direction  = p.Direction  ?? "";
            port.DataType   = p.DataType   ?? "BOOL";
            port.TagPattern = p.TagPattern ?? "";
            port.IsDummy    = p.IsDummy;
            port.AddressOffset =
                p.AddressOffset.HasValue
                    ? FSharpOption<int>.Some(p.AddressOffset.Value)
                    : FSharpOption<int>.None;
            corePreset.FBTagMapTemplate.Add(port);
        }
        foreach (var akv in preset.AutoAuxPortMap ?? new Dictionary<string, string>())
            corePreset.AutoAuxPortMap[akv.Key] = akv.Value;
        foreach (var ckv in preset.ComAuxPortMap ?? new Dictionary<string, string>())
            corePreset.ComAuxPortMap[ckv.Key] = ckv.Value;
        foreach (var e in preset.IwPatterns ?? new List<SignalPatternEntryDto>())
            corePreset.IwPatterns.Add(FromDtoSig(e));
        foreach (var e in preset.QwPatterns ?? new List<SignalPatternEntryDto>())
            corePreset.QwPatterns.Add(FromDtoSig(e));
        foreach (var e in preset.MwPatterns ?? new List<SignalPatternEntryDto>())
            corePreset.MwPatterns.Add(FromDtoSig(e));
        cp.FBTagMapPresets[deviceType] = corePreset;
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
        ApiName      = e.ApiName ?? "",
        Pattern      = e.Pattern ?? "",
        TargetFBPort = e.TargetFBPort ?? "",
    };

    private static SignalPatternEntry FromDtoSig(SignalPatternEntryDto d) => new()
    {
        ApiName      = d.ApiName ?? "",
        Pattern      = d.Pattern ?? "",
        TargetFBPort = d.TargetFBPort ?? "",
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
    public List<FBTagMapPortDto> Ports { get; set; } = new();
    public string AddressRule { get; set; } = "Sequential";
    public BaseAddressDto? BaseAddresses { get; set; } = new();
    public Dictionary<string, string> AutoAuxPortMap { get; set; } = new();
    public Dictionary<string, string> ComAuxPortMap { get; set; } = new();
    public List<SignalPatternEntryDto> IwPatterns { get; set; } = new();
    public List<SignalPatternEntryDto> QwPatterns { get; set; } = new();
    public List<SignalPatternEntryDto> MwPatterns { get; set; } = new();
}

public class SignalPatternEntryDto
{
    public string ApiName      { get; set; } = "";
    public string Pattern      { get; set; } = "";
    public string TargetFBPort { get; set; } = "";
}

public class BaseAddressDto
{
    public string InputBase  { get; set; } = "%IW0.0.0";
    public string OutputBase { get; set; } = "%QW0.0.0";
    public string MemoryBase { get; set; } = "%MW100";
}

public class FBTagMapPortDto
{
    public string FBPort     { get; set; } = "";
    public string Direction  { get; set; } = "Input";
    public string DataType   { get; set; } = "BOOL";
    public string TagPattern { get; set; } = "";
    public bool   IsDummy    { get; set; } = false;
    public int?   AddressOffset { get; set; }
}
