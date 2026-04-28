using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.FSharp.Collections;
using Plc.Xgi;

namespace Promaker.Services;

/// <summary>
/// XGI_Template.xml 로부터 FB 타입 + Local Label 목록을 읽어
/// Wizard Step 4 의 콤보박스 데이터소스로 제공.
/// Direction 필터 없음 — 모든 포트(Input+Output) 가 한 리스트.
/// </summary>
public static class FBPortCatalog
{
    private static Dictionary<string, List<string>>? _cache;

    /// <summary>XGI_Template.xml 기본 경로 (빌드 출력 폴더).</summary>
    public static string DefaultTemplatePath =>
        Path.Combine(System.AppContext.BaseDirectory, "Template", "XGI_Template.xml");

    /// <summary>FB 타입명 목록 (콤보 1 데이터소스).</summary>
    public static IReadOnlyList<string> GetFBTypeNames(string? xmlPath = null)
    {
        EnsureLoaded(xmlPath);
        return _cache!.Keys.OrderBy(x => x).ToList();
    }

    /// <summary>선택한 FB 의 모든 Local Label (콤보 2 데이터소스) — Direction 필터 없음.</summary>
    public static IReadOnlyList<string> GetLocalLabels(string fbTypeName, string? xmlPath = null)
    {
        EnsureLoaded(xmlPath);
        if (string.IsNullOrEmpty(fbTypeName)) return System.Array.Empty<string>();
        return _cache!.TryGetValue(fbTypeName, out var labels)
            ? labels
            : (IReadOnlyList<string>)System.Array.Empty<string>();
    }

    /// <summary>강제 재로드 (템플릿 파일이 교체되었을 때).</summary>
    public static void Reload(string? xmlPath = null)
    {
        _cache = null;
        EnsureLoaded(xmlPath);
    }

    private static void EnsureLoaded(string? xmlPath)
    {
        if (_cache != null) return;
        var path = xmlPath ?? DefaultTemplatePath;
        var map = FBPortReader.readFromXml(path);
        _cache = new Dictionary<string, List<string>>();
        foreach (var kv in map)
        {
            var labels = new List<string>();
            foreach (var p in ListModule.ToSeq(kv.Value.InputPorts))  labels.Add(p.Name);
            foreach (var p in ListModule.ToSeq(kv.Value.OutputPorts)) labels.Add(p.Name);
            _cache[kv.Key] = labels;
        }
    }
}
