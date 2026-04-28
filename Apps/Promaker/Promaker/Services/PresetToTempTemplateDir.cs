using System;
using System.IO;
using System.Linq;
using System.Text;
using Ds2.Core;
using Ds2.Core.Store;
using Microsoft.FSharp.Core;

namespace Promaker.Services;

/// <summary>
/// AASX 내 FBTagMapPresets 데이터를 일시 임시 디렉토리에 txt 로 전개한다.
/// <see cref="IoListPipeline"/> 가 아직 파일 경로를 입력으로 요구하기 때문.
/// using 범위를 벗어나면 디렉토리 전체가 삭제되어 영구 파일이 남지 않는다.
///
/// 동일 AASX = 동일 출력 보장: 매 호출마다 Preset 에서 재생성하므로
/// 이전 세션이 남긴 파일이나 사용자 편집 파일의 영향을 받지 않는다.
/// </summary>
public sealed class PresetToTempTemplateDir : IDisposable
{
    public string Path { get; }

    private PresetToTempTemplateDir(string path) { Path = path; }

    public static PresetToTempTemplateDir Materialize(DsStore store)
    {
        var dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "TagWizard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        // system_base.txt / flow_base.txt / <SystemType>.txt 를 Preset 기반으로 전개
        WriteSystemBase(store, dir);
        WriteFlowBase  (store, dir);
        WriteDeviceTemplates(store, dir);

        return new PresetToTempTemplateDir(dir);
    }

    /// 단일 패턴 엔트리를 txt 라인으로 기록.
    /// ApiName == "-" → 빈 슬롯 ('-' 단독 라인) 으로 emit, 주소 1 비트만 예약.
    /// ApiName 가 빈 문자열 → emit 생략 (legacy 호환).
    private static void EmitEntry(StringBuilder sb, string api, string pat)
    {
        if (string.IsNullOrEmpty(api)) return;
        if (api == "-") { sb.AppendLine("-"); return; }
        sb.AppendLine($"{api}: {pat}");
    }

    private static void WriteSystemBase(DsStore store, string dir)
    {
        var presets = FBTagMapStore.LoadAll(store);
        if (presets.Count == 0) return;

        var sb = new StringBuilder();
        sb.AppendLine("# auto-generated from FBTagMapPresets (transient)");
        foreach (var kv in presets)
        {
            var ba = kv.Value.BaseAddresses;
            if (ba == null) continue;
            sb.AppendLine($"@SYSTEM {kv.Key}");
            if (TryNum(ba.InputBase,  out var iw)) sb.AppendLine($"@IW_BASE {iw}");
            if (TryNum(ba.OutputBase, out var qw)) sb.AppendLine($"@QW_BASE {qw}");
            if (TryNum(ba.MemoryBase, out var mw)) sb.AppendLine($"@MW_BASE {mw}");
            sb.AppendLine();
        }
        File.WriteAllText(System.IO.Path.Combine(dir, "system_base.txt"), sb.ToString(), Encoding.UTF8);
    }

    private static void WriteFlowBase(DsStore store, string dir)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# auto-generated from ControlFlowProperties.BaseAddressOverride (transient)");
        bool any = false;
        foreach (var flow in store.Flows.Values)
        {
            var cfpOpt = flow.GetControlProperties();
            if (!FSharpOption<ControlFlowProperties>.get_IsSome(cfpOpt)) continue;
            var ov = cfpOpt.Value.BaseAddressOverride;
            if (!FSharpOption<FBBaseAddressSet>.get_IsSome(ov)) continue;
            var ba = ov.Value;
            sb.AppendLine($"@FLOW {flow.Name}");
            if (TryNum(ba.InputBase,  out var iw)) sb.AppendLine($"@IW_BASE {iw}");
            if (TryNum(ba.OutputBase, out var qw)) sb.AppendLine($"@QW_BASE {qw}");
            if (TryNum(ba.MemoryBase, out var mw)) sb.AppendLine($"@MW_BASE {mw}");
            sb.AppendLine();
            any = true;
        }
        if (any)
            File.WriteAllText(System.IO.Path.Combine(dir, "flow_base.txt"), sb.ToString(), Encoding.UTF8);
    }

    private static void WriteDeviceTemplates(DsStore store, string dir)
    {
        var presets = FBTagMapStore.LoadAll(store);
        var emitted = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // (1) Preset 이 존재하는 SystemType — 패턴이 있으면 그대로, 없으면 임베디드 기본값
        foreach (var kv in presets)
        {
            var sysType = kv.Key;
            var preset  = kv.Value;
            var iw = preset.IwPatterns.Select(p => (p.ApiName, p.Pattern)).ToList();
            var qw = preset.QwPatterns.Select(p => (p.ApiName, p.Pattern)).ToList();
            var mw = preset.MwPatterns.Select(p => (p.ApiName, p.Pattern)).ToList();

            if (iw.Count == 0 && qw.Count == 0 && mw.Count == 0)
            {
                if (TryWriteDefaultFor(sysType, dir)) { emitted.Add(sysType); continue; }
            }

            var sb = new StringBuilder();
            sb.AppendLine($"# auto-generated from FBTagMapPresets[{sysType}] (transient)");
            sb.AppendLine();
            if (iw.Count > 0)
            {
                sb.AppendLine("[IW]");
                foreach (var (api, pat) in iw)
                    EmitEntry(sb, api, pat);
                sb.AppendLine();
            }
            if (qw.Count > 0)
            {
                sb.AppendLine("[QW]");
                foreach (var (api, pat) in qw)
                    EmitEntry(sb, api, pat);
                sb.AppendLine();
            }
            if (mw.Count > 0)
            {
                sb.AppendLine("[MW]");
                foreach (var (api, pat) in mw)
                    EmitEntry(sb, api, pat);
            }
            File.WriteAllText(System.IO.Path.Combine(dir, sysType + ".txt"), sb.ToString(), Encoding.UTF8);
            emitted.Add(sysType);
        }

        // (2) store 에 등장하는 모든 SystemType + 프로젝트 프리셋의 SystemType — 아직 emit 하지 않은 것 fallback
        foreach (var sysType in UsedSystemTypes(store))
            if (!emitted.Contains(sysType) && WritePresetDefault(sysType, dir))
                emitted.Add(sysType);
        foreach (var sysType in SystemTypePresetProvider.GetSystemTypes())
            if (!emitted.Contains(sysType) && WritePresetDefault(sysType, dir))
                emitted.Add(sysType);
    }

    /// <summary>
    /// 프로젝트 프리셋에 등록된 ApiNames 로 기본 IW/QW/MW 템플릿을 합성해 txt 파일로 기록.
    /// API 가 없으면 기존 DefaultTemplatesRO fallback.
    /// </summary>
    private static bool WritePresetDefault(string systemType, string dir)
    {
        var apis = SystemTypePresetProvider.GetApiNames(systemType);
        if (apis.Length == 0)
            return TryWriteDefaultFor(systemType, dir);

        var sb = new StringBuilder();
        sb.AppendLine($"# auto-generated for SystemType '{systemType}' (from project preset)");
        sb.AppendLine();
        sb.AppendLine("[IW]");
        foreach (var a in apis) sb.AppendLine($"{a}: W_$(F)_WRS_$(D)_$(A)");
        sb.AppendLine();
        sb.AppendLine("[QW]");
        foreach (var a in apis) sb.AppendLine($"{a}: W_$(F)_SOL_$(D)_$(A)");
        sb.AppendLine();
        sb.AppendLine("[MW]");
        foreach (var a in apis) sb.AppendLine($"{a}: W_$(F)_M_$(D)_$(A)");
        File.WriteAllText(System.IO.Path.Combine(dir, systemType + ".txt"), sb.ToString(), Encoding.UTF8);
        return true;
    }

    private static System.Collections.Generic.IEnumerable<string> UsedSystemTypes(DsStore store)
    {
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sys in store.Systems.Values)
        {
            var opt = sys.SystemType;
            if (opt != null && FSharpOption<string>.get_IsSome(opt))
            {
                var t = opt.Value;
                if (!string.IsNullOrWhiteSpace(t) && seen.Add(t))
                    yield return t;
            }
        }
    }

    private static bool TryWriteDefaultFor(string systemType, string dir)
    {
        var key = systemType + ".txt";
        if (!TemplateManager.DefaultTemplatesRO.TryGetValue(key, out var content)) return false;
        File.WriteAllText(System.IO.Path.Combine(dir, key), content, Encoding.UTF8);
        return true;
    }

    private static bool TryNum(string? s, out int n)
    {
        n = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var m = System.Text.RegularExpressions.Regex.Match(s, @"\d+");
        return m.Success && int.TryParse(m.Value, out n);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
        catch { /* best-effort cleanup */ }
    }
}
