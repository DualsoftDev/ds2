using System.Text.Json.Serialization;

namespace Promaker.Dialogs.ConfigEditor.Models;

/// <summary>
/// 필터 제외 키워드 설정 (JSON config 파일에서 로드)
/// </summary>
public class FilterExclusionsConfig
{
    [JsonPropertyName("Description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("DeviceKeywords")]
    public List<string> DeviceKeywords { get; set; } = new();

    [JsonPropertyName("ApiKeywords")]
    public List<string> ApiKeywords { get; set; } = new();

    [JsonPropertyName("FlowKeywords")]
    public List<string> FlowKeywords { get; set; } = new();
}

/// <summary>
/// Flow 필터링 설정: 비어있으면 S{숫자} 패턴 사용, 항목 있으면 해당 Flow만 체크됨
/// </summary>
public class FlowInclusionsConfig
{
    [JsonPropertyName("Description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("Flows")]
    public List<string> Flows { get; set; } = new();
}

/// <summary>
/// input-matching-config.json의 루트 구조
/// </summary>
public class InputMatchingConfigRoot
{
    [JsonPropertyName("FilterExclusions")]
    public FilterExclusionsConfig FilterExclusions { get; set; } = new();

    [JsonPropertyName("FlowInclusions")]
    public FlowInclusionsConfig FlowInclusions { get; set; } = new();
}
