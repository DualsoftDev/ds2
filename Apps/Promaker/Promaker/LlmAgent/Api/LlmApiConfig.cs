using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using log4net;

namespace Promaker.LlmAgent.Api;

/// <summary>
/// API 기반 provider (Anthropic / OpenAI / Ollama) 의 user-scope 설정 holder.
///
/// **저장 위치**: `%APPDATA%/Promaker/llm-api-config.json`. consent 와 동일 디렉토리, 별도 파일.
/// **암호화**: API key 만 DPAPI (`ProtectedData.Protect` <see cref="DataProtectionScope.CurrentUser"/>) 로
/// 암호화 후 base64. 다른 사용자 / 다른 머신에서 평문 disk read 만으로는 복호화 불가.
/// 모델명 / base URL 등 비민감 필드는 평문.
/// </summary>
public sealed class LlmApiConfig
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(LlmApiConfig));

    /// <summary>각 provider 의 API key 암호화 base64. 평문 진입점은 <see cref="GetApiKey"/> / <see cref="SetApiKey"/>.</summary>
    [JsonPropertyName("encryptedKeys")]
    public Dictionary<string, string> EncryptedKeys { get; set; } = new();

    [JsonPropertyName("anthropicModel")]
    public string AnthropicModel { get; set; } = "claude-sonnet-4-6";

    [JsonPropertyName("openAiModel")]
    public string OpenAiModel { get; set; } = "gpt-4o";

    [JsonPropertyName("ollamaModel")]
    public string OllamaModel { get; set; } = "llama3.1";

    [JsonPropertyName("ollamaBaseUrl")]
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";

    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("Promaker.LlmApi.v1");

    public static string ConfigPath
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "Promaker", "llm-api-config.json");
        }
    }

    public static LlmApiConfig Load()
    {
        try
        {
            var path = ConfigPath;
            if (!File.Exists(path)) return new LlmApiConfig();
            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<LlmApiConfig>(json) ?? new LlmApiConfig();
        }
        catch (Exception ex)
        {
            Log.Warn($"LlmApiConfig.Load 실패 — 기본값 사용: {ex.Message}");
            return new LlmApiConfig();
        }
    }

    public void Save()
    {
        var path = ConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, path, overwrite: true);
    }

    public string? GetApiKey(string providerKey)
    {
        if (!EncryptedKeys.TryGetValue(providerKey, out var enc) || string.IsNullOrEmpty(enc))
            return null;
        try
        {
            var encrypted = Convert.FromBase64String(enc);
            var plain = ProtectedData.Unprotect(encrypted, DpapiEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            Log.Warn($"LlmApiConfig.GetApiKey({providerKey}) 복호화 실패: {ex.Message}");
            return null;
        }
    }

    public void SetApiKey(string providerKey, string? apiKey)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            EncryptedKeys.Remove(providerKey);
            return;
        }
        var plain = Encoding.UTF8.GetBytes(apiKey);
        var encrypted = ProtectedData.Protect(plain, DpapiEntropy, DataProtectionScope.CurrentUser);
        EncryptedKeys[providerKey] = Convert.ToBase64String(encrypted);
    }

    public bool HasApiKey(string providerKey) => !string.IsNullOrEmpty(GetApiKey(providerKey));
}
