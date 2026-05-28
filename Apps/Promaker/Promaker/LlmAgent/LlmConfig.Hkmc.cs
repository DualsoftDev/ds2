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
/// H-Chat 게이트웨이 endpoint / 모델 설정. 모델 ID 는 default 빈 문자열 — 사용자 입력 강제.
/// </summary>
public sealed class HkmcHChatConfig
{
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "https://internal-apigw-kr.hmg-corp.io/hchat-in/api";

    [JsonPropertyName("claudeModel")]
    public string ClaudeModel { get; set; } = "";

    [JsonPropertyName("openAiModel")]
    public string OpenAiModel { get; set; } = "";
}
