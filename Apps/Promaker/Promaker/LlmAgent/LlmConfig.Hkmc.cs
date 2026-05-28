using System.Text.Json.Serialization;

namespace Promaker.LlmAgent;

public sealed partial class LlmConfig
{
    /// <summary>
    /// HKMC H-Chat 게이트웨이 설정 블록. ENABLE_HKMC off 사용자의 disk JSON 에는 null 직렬화로 흔적 없음.
    /// API key 는 본 블록이 아닌 <see cref="EncryptedKeys"/> dict 의 "hkmc-hchat" 슬롯에 보관.
    /// </summary>
    [JsonPropertyName("hkmcHChat")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HkmcHChatConfig? HkmcHChat { get; set; }
}

/// <summary>
/// H-Chat 게이트웨이 endpoint / 모델 설정. 모델 ID default 는 H-Chat docs (Docs4) 의 latest stable —
/// Settings panel 의 후보 dropdown 첫 항목과 정합. 사용자가 비울 경우는 declined 분기로 안내.
/// </summary>
public sealed class HkmcHChatConfig
{
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "***REDACTED-INTERNAL-URL***";

    [JsonPropertyName("claudeModel")]
    public string ClaudeModel { get; set; } = "claude-sonnet-4-6";

    [JsonPropertyName("openAiModel")]
    public string OpenAiModel { get; set; } = "gpt-5.4";
}
