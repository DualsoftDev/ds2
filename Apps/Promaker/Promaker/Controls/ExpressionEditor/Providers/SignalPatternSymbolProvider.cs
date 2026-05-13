using System;
using System.Collections.Generic;
using System.Linq;
using AAStoPLC.TagWizard;
using Ds2.Core.Store;

namespace Promaker.Controls.ExpressionEditor.Providers;

/// <summary>
/// Pre-FB 조건 편집용 심볼 제공자.
/// 후보:
///   • 현 SystemType 의 모든 IW/QW/MW Pattern (매크로 미치환 형태 — 예: "W_$(F)_$(D)_HMI_PB")
///   • 모든 Call 의 FB OUT 라벨 — QwPatterns + MwPatterns 의 매크로 치환된 실제 변수명
///     (예: "W_S113_LW_LATCH_M_Adv_End"). 인과 결과를 조건으로 넣을 때 사용.
/// 자유 텍스트 입력 허용 — 다른 시스템의 변수명도 직접 타이핑 가능.
/// </summary>
public sealed class SignalPatternSymbolProvider : IExpressionSymbolProvider
{
    private readonly List<SymbolCandidate> _candidates;

    public SignalPatternSymbolProvider(DsStore store, string systemType)
    {
        _candidates = BuildCandidates(store, systemType);
    }

    public IReadOnlyList<SymbolCandidate> GetCandidates() => _candidates;

    public bool IsValid(string symbol, Guid? refKey) =>
        !string.IsNullOrWhiteSpace(symbol);  // Pre-FB 는 자유 입력이라 비어있지만 않으면 OK

    public bool AllowsFreeText => true;

    private static List<SymbolCandidate> BuildCandidates(DsStore store, string systemType)
    {
        var result = new List<SymbolCandidate>();
        if (string.IsNullOrWhiteSpace(systemType)) return result;

        var presets = FBTagMapStore.LoadAll(store);
        if (!presets.TryGetValue(systemType, out var preset) || preset == null)
            return result;

        void AddPatterns(IEnumerable<SignalPatternEntryDto> entries, string group)
        {
            foreach (var e in entries)
            {
                if (e == null || string.IsNullOrWhiteSpace(e.Pattern)) continue;
                if (e.IsSpare) continue;
                result.Add(new SymbolCandidate(e.Pattern, null, group));
            }
        }

        AddPatterns(preset.IwPatterns, "IW");
        AddPatterns(preset.QwPatterns, "QW");
        AddPatterns(preset.MwPatterns, "MW");

        // 중복 제거 (동일 Pattern 이 여러 섹션에 있을 수 있음).
        var deduped = result
            .GroupBy(c => c.Display, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(c => c.Display, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 최상단에 "User입력필요" 직관적 태그 — 사용자가 정의하지 않은 외부 신호 자리표시자.
        // 선택 후 콤보에서 직접 타이핑으로 실제 변수명으로 교체 가능 (IsEditable=True).
        // PLC 출력 시 BOOL 무주소 글로벌로 자동 등록 (Emit.fs userExprVars).
        deduped.Insert(0, new SymbolCandidate("User입력필요", null, "User"));

        // 모든 Call 의 FB OUT 라벨 — 인과 조건으로 선택. 매크로 치환된 실제 변수명.
        deduped.AddRange(BuildFbOutCandidates(store, presets));
        return deduped;
    }

    /// <summary>모든 Call × QwPatterns/MwPatterns(TargetFBPort 비어있지 않은 항목) 의 매크로 치환된 변수명을 후보로 enumerate.</summary>
    private static List<SymbolCandidate> BuildFbOutCandidates(
        DsStore store, IReadOnlyDictionary<string, FBTagMapPresetDto> presets)
    {
        var output = new List<SymbolCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var call in store.Calls.Values)
        {
            var sysType = ResolveCallSystemType(store, call);
            if (string.IsNullOrEmpty(sysType)) continue;
            if (!presets.TryGetValue(sysType, out var preset) || preset == null) continue;

            var (flowName, _) = FlowWorkName(store, call);
            var sysName = ActiveSystemName(store, call);
            var device = call.DevicesAlias ?? "";
            var groupLabel = $"FB OUT / {(string.IsNullOrEmpty(flowName) ? "?" : flowName)} {device}";

            void EmitFromSection(IEnumerable<SignalPatternEntryDto> entries)
            {
                foreach (var e in entries)
                {
                    if (e == null || e.IsSpare) continue;
                    if (string.IsNullOrWhiteSpace(e.Pattern)) continue;
                    if (string.IsNullOrWhiteSpace(e.TargetFBPort)) continue;
                    var resolved = Substitute(e.Pattern, flowName, device, e.ApiName ?? "", sysName);
                    if (string.IsNullOrWhiteSpace(resolved)) continue;
                    if (seen.Add(resolved))
                        output.Add(new SymbolCandidate(resolved, null, groupLabel));
                }
            }
            EmitFromSection(preset.QwPatterns);
            EmitFromSection(preset.MwPatterns);
        }
        return output;
    }

    private static string ResolveCallSystemType(DsStore store, Ds2.Core.Call call)
    {
        foreach (var ac in call.ApiCalls)
        {
            if (ac.ApiDefId == null || !Microsoft.FSharp.Core.FSharpOption<Guid>.get_IsSome(ac.ApiDefId)) continue;
            var apiDefId = ac.ApiDefId.Value;
            if (!store.ApiDefs.TryGetValue(apiDefId, out var def)) continue;
            if (!store.Systems.TryGetValue(def.ParentId, out var sys)) continue;
            if (sys.SystemType != null
                && Microsoft.FSharp.Core.FSharpOption<string>.get_IsSome(sys.SystemType)
                && !string.IsNullOrEmpty(sys.SystemType.Value))
                return sys.SystemType.Value;
        }
        return "";
    }

    private static (string flow, string work) FlowWorkName(DsStore store, Ds2.Core.Call call)
    {
        if (!store.Works.TryGetValue(call.ParentId, out var work)) return ("", "");
        if (!store.Flows.TryGetValue(work.ParentId, out var flow)) return ("", work.Name ?? "");
        return (flow.Name ?? "", work.Name ?? "");
    }

    private static string ActiveSystemName(DsStore store, Ds2.Core.Call call)
    {
        if (!store.Works.TryGetValue(call.ParentId, out var work)) return "";
        if (!store.Flows.TryGetValue(work.ParentId, out var flow)) return "";
        if (!store.Systems.TryGetValue(flow.ParentId, out var sys)) return "";
        return sys.Name ?? "";
    }

    private static string Substitute(string pattern, string flow, string device, string api, string system)
    {
        if (string.IsNullOrEmpty(pattern)) return "";
        return pattern
            .Replace("$(F)", flow ?? "")
            .Replace("$(D)", device ?? "")
            .Replace("$(A)", api ?? "")
            .Replace("$(S)", system ?? "");
    }
}
