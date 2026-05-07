using System.Collections.Generic;
using System.IO;

namespace Promaker.Services;

/// <summary>
/// XGI_Template.xml 의 FB 정의를 UI 콤보용 형태로 노출.
/// 캐시 / 실제 lookup 은 <see cref="AAStoPLC.TagWizard.FBPortLookup"/>(F#) 으로 이관됨.
/// 이 thin shim 은 Promaker 의 SettingsPaths / XgiTemplateExtractor 와 통합해
/// 기본 경로 자동 추출만 담당.
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
                AAStoPLC.TagWizard.FBPortLookup.SetDefaultTemplatePath(SettingsPaths.DefaultXgiTemplate);
            }
            return SettingsPaths.DefaultXgiTemplate;
        }
    }
    private static bool _extractAttempted;

    public static IReadOnlyList<string> GetFBTypeNames(string? xmlPath = null) =>
        AAStoPLC.TagWizard.FBPortLookup.GetFBTypeNames(xmlPath ?? DefaultTemplatePath);

    public static IReadOnlyList<string> GetLocalLabels(string fbTypeName, string? xmlPath = null) =>
        AAStoPLC.TagWizard.FBPortLookup.GetLocalLabels(fbTypeName, xmlPath ?? DefaultTemplatePath);

    public static (IReadOnlyList<string> Inputs, IReadOnlyList<string> Outputs) GetPortsByDirection(
        string fbTypeName, string? xmlPath = null)
    {
        var t = AAStoPLC.TagWizard.FBPortLookup.GetPortsByDirection(fbTypeName, xmlPath ?? DefaultTemplatePath);
        return (t.Item1, t.Item2);
    }

    public static IReadOnlyDictionary<string, string> GetPortTypeMap(string fbTypeName, string? xmlPath = null) =>
        AAStoPLC.TagWizard.FBPortLookup.GetPortTypeMap(fbTypeName, xmlPath ?? DefaultTemplatePath);

    public static void Reload(string? xmlPath = null) =>
        AAStoPLC.TagWizard.FBPortLookup.Reload(xmlPath ?? DefaultTemplatePath);
}
