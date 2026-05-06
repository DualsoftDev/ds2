using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using log4net;

namespace Promaker.Services;

/// <summary>
/// SystemType 별 FBTagMap 디폴트 — AAStoPLC.dll 의 EmbeddedResource (타입당 1개 JSON).
/// 리소스 이름 패턴: "AAStoPLC.FBTagMapDefaults.&lt;SystemType&gt;.json"
/// 디스크 사본은 사용하지 않음 — 임베디드만이 진실원.
/// </summary>
public static class FBTagMapEmbeddedDefaults
{
    private static readonly ILog Log = LogManager.GetLogger("FBTagMapEmbeddedDefaults");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
    };

    private const string ResourcePrefix = "AAStoPLC.FBTagMapDefaults.";
    private const string ResourceSuffix = ".json";

    private static System.Reflection.Assembly ResourceAssembly =>
        typeof(Plc.Xgi.SignalPipelineV2.SignalRow).Assembly;

    /// <summary>리소스 이름 → SystemType lookup 테이블.</summary>
    private static readonly Lazy<IReadOnlyDictionary<string, string>> ResourceMap =
        new(BuildResourceMap, isThreadSafe: true);

    private static IReadOnlyDictionary<string, string> BuildResourceMap()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in ResourceAssembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.EndsWith(ResourceSuffix, StringComparison.OrdinalIgnoreCase)) continue;
            var sysType = name.Substring(ResourcePrefix.Length,
                name.Length - ResourcePrefix.Length - ResourceSuffix.Length);
            dict[sysType] = name;
        }
        return dict;
    }

    /// <summary>SystemType 디폴트 DTO. 리소스 부재 시 null.</summary>
    public static FBTagMapPresetDto? Load(string sysType)
    {
        if (string.IsNullOrEmpty(sysType)) return null;
        if (!ResourceMap.Value.TryGetValue(sysType, out var resourceName)) return null;
        try
        {
            using var stream = ResourceAssembly.GetManifestResourceStream(resourceName);
            if (stream == null) return null;
            using var reader = new StreamReader(stream);
            return JsonSerializer.Deserialize<FBTagMapPresetDto>(reader.ReadToEnd(), JsonOpts);
        }
        catch (Exception ex)
        {
            Log.Warn($"FBTagMap 임베디드 디폴트 로드 실패 ({sysType}): {ex.Message}");
            return null;
        }
    }

    /// <summary>임베디드 디폴트가 정의된 SystemType 목록.</summary>
    public static IEnumerable<string> KnownSystemTypes() => ResourceMap.Value.Keys;
}
