using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Plc.Xgi;

namespace Promaker.Services;

/// <summary>
/// XGI_Template.xml 의 FB 정의를 UI 콤보용 형태로 노출.
/// 캐시는 F# FBPortRegistry 가 보유 — 여기서는 path 해석 + 표시 형태 변환만 담당.
/// </summary>
public static class FBPortCatalog
{
    /// <summary>
    /// XGI_Template.xml 기본 경로 — AppData 사본 (없으면 임베디드 리소스에서 1회 추출).
    /// 추출 시도는 첫 호출 1회만 — 이후엔 path 문자열만 즉시 반환 (퍼포먼스).
    /// </summary>
    public static string DefaultTemplatePath
    {
        get
        {
            if (!_extractAttempted)
            {
                _extractAttempted = true;
                if (!File.Exists(SettingsPaths.DefaultXgiTemplate))
                    XgiTemplateExtractor.ExtractIfMissing(SettingsPaths.DefaultXgiTemplate);
            }
            return SettingsPaths.DefaultXgiTemplate;
        }
    }
    private static bool _extractAttempted;

    /// <summary>FB 타입명 목록 (정렬).</summary>
    public static IReadOnlyList<string> GetFBTypeNames(string? xmlPath = null) =>
        FBPortRegistry.knownFbNames(xmlPath ?? DefaultTemplatePath).ToList();

    // ── 캐시 ─────────────────────────────────────────────────────────────────
    // 그리드의 매 row 가 PortOptions getter 를 호출하므로 (Robot 의 경우 ~400 회/load)
    // 결과를 fbType 키로 캐시해 List 재할당 + LINQ 비용 제거.
    private static readonly Dictionary<string, IReadOnlyList<string>> _localLabelsCache = new();
    private static readonly Dictionary<string, (IReadOnlyList<string> Inputs, IReadOnlyList<string> Outputs)>
        _byDirCache = new();

    /// <summary>FB 의 모든 Local Label (Input + Output 합본). 캐시됨.</summary>
    public static IReadOnlyList<string> GetLocalLabels(string fbTypeName, string? xmlPath = null)
    {
        if (string.IsNullOrEmpty(fbTypeName)) return Array.Empty<string>();
        if (_localLabelsCache.TryGetValue(fbTypeName, out var hit)) return hit;

        var defsOpt = FBPortRegistry.tryGetFb(xmlPath ?? DefaultTemplatePath, fbTypeName);
        IReadOnlyList<string> result = FSharpOption<FBPortDefs>.get_IsNone(defsOpt)
            ? (IReadOnlyList<string>)Array.Empty<string>()
            : defsOpt.Value.InputPorts.Select(p => p.Name)
                .Concat(defsOpt.Value.OutputPorts.Select(p => p.Name))
                .ToList();
        _localLabelsCache[fbTypeName] = result;
        return result;
    }

    /// <summary>FB 의 방향별 포트 이름. 캐시됨.</summary>
    public static (IReadOnlyList<string> Inputs, IReadOnlyList<string> Outputs) GetPortsByDirection(
        string fbTypeName, string? xmlPath = null)
    {
        if (string.IsNullOrEmpty(fbTypeName))
            return (Array.Empty<string>(), Array.Empty<string>());
        if (_byDirCache.TryGetValue(fbTypeName, out var hit)) return hit;

        var defsOpt = FBPortRegistry.tryGetFb(xmlPath ?? DefaultTemplatePath, fbTypeName);
        var pair = FSharpOption<FBPortDefs>.get_IsNone(defsOpt)
            ? ((IReadOnlyList<string>)Array.Empty<string>(), (IReadOnlyList<string>)Array.Empty<string>())
            : ((IReadOnlyList<string>)defsOpt.Value.InputPorts.Select(p => p.Name).ToList(),
               (IReadOnlyList<string>)defsOpt.Value.OutputPorts.Select(p => p.Name).ToList());
        _byDirCache[fbTypeName] = pair;
        return pair;
    }

    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> _typeMapCache = new();

    /// <summary>FB 타입의 (포트 이름 → IEC 타입명) lookup. Input/Output 합본, 캐시됨.</summary>
    public static IReadOnlyDictionary<string, string> GetPortTypeMap(string fbTypeName, string? xmlPath = null)
    {
        if (string.IsNullOrEmpty(fbTypeName))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_typeMapCache.TryGetValue(fbTypeName, out var hit)) return hit;
        var defsOpt = FBPortRegistry.tryGetFb(xmlPath ?? DefaultTemplatePath, fbTypeName);
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (FSharpOption<FBPortDefs>.get_IsSome(defsOpt))
        {
            foreach (var p in defsOpt.Value.InputPorts)  dict[p.Name] = p.TypeName ?? "";
            foreach (var p in defsOpt.Value.OutputPorts) dict[p.Name] = p.TypeName ?? "";
        }
        _typeMapCache[fbTypeName] = dict;
        return dict;
    }

    /// <summary>강제 재로드 — 템플릿 파일 교체 시.</summary>
    public static void Reload(string? xmlPath = null)
    {
        _localLabelsCache.Clear();
        _byDirCache.Clear();
        _typeMapCache.Clear();
        FBPortRegistry.invalidate(xmlPath ?? DefaultTemplatePath);
    }
}
