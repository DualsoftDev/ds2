using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using log4net;
using Promaker.Services;

namespace Promaker.LlmAgent;

/// <summary>
/// Promaker LLM 사용자 설정 — Consent + Provider 통합.
///
/// **저장 위치**: <see cref="SettingsPaths"/>.Of("llm-config.json")
///   = `%APPDATA%\Dualsoft\Promaker\Settings\llm-config.json`
/// (다른 Promaker app-scope 설정과 같은 디렉토리. 배포 전이라 마이그레이션 코드 없음.)
///
/// **암호화**: API key 만 DPAPI (`ProtectedData.Protect` <see cref="DataProtectionScope.CurrentUser"/>) +
/// entropy "Promaker.LlmApi.v1" + base64. 다른 사용자 / 다른 머신에서 평문 disk read 만으로는 복호화 불가.
/// 모델명 / Ollama base URL / Consent / DefaultProvider 는 평문.
///
/// 통합 (2026-05-07): 이전 `LlmConsent.cs` (static, `%APPDATA%\Promaker\llm-config.json`) +
/// `LlmApiConfig.cs` (instance, `%APPDATA%\Promaker\llm-api-config.json`) → 본 단일 클래스.
/// JSON 은 flat 8 필드 (consent 2 + provider 6). 사용자 인지 부하 ↓ + 향후 추가 필드의 위치 결정 회의 X.
/// </summary>
public sealed class LlmConfig
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(LlmConfig));
    private static readonly object _saveLock = new();
    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("Promaker.LlmApi.v1");

    public static string ConfigPath => SettingsPaths.Of("llm-config.json");

    // ─── Consent ─────────────────────────────────────────────────────────────

    [JsonPropertyName("dataEgressConsent")]
    public bool DataEgressConsent { get; set; }

    [JsonPropertyName("consentTimestampUtc")]
    public string? ConsentTimestampUtc { get; set; }

    // ─── Provider settings ───────────────────────────────────────────────────

    /// <summary>시작 시 ComboBox 의 초기 선택값. enum 이름 (Claude / Codex / AnthropicApi / OpenAiApi / Ollama).</summary>
    [JsonPropertyName("defaultProvider")]
    public string DefaultProvider { get; set; } = "Claude";

    /// <summary>provider key (anthropic / openai) → DPAPI 암호화 base64.</summary>
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

    // ─── I/O ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Corrupt JSON 시 LLM Chat 영구 차단 회피: `.bak` 백업 후 default 반환.
    /// 다음 Save 시 새 정상 파일이 작성되어 사용자가 동의 다이얼로그를 다시 거치게 됨 (이전 LlmConsent.cs M2 정책 보존).
    /// </summary>
    public static LlmConfig Load()
    {
        var path = ConfigPath;
        if (!File.Exists(path)) return new LlmConfig();
        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<LlmConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            }) ?? new LlmConfig();
        }
        catch (JsonException ex)
        {
            var bak = path + ".bak";
            try { File.Move(path, bak, overwrite: true); } catch { /* best-effort */ }
            Log.Warn($"LlmConfig JSON corrupt — {bak} 로 백업 후 default 사용 ({ex.Message})");
            return new LlmConfig();
        }
        catch (Exception ex)
        {
            Log.Warn($"LlmConfig.Load 실패 — 기본값 사용: {ex.Message}");
            return new LlmConfig();
        }
    }

    /// <summary>
    /// Promaker 다중 인스턴스 동시 save race 방지: lock + atomic write (`.tmp-<pid>` + Move overwrite).
    /// 이전 LlmConsent.Save M2 정책 + LlmApiConfig.Save atomic 패턴 통합.
    /// </summary>
    public void Save()
    {
        lock (_saveLock)
        {
            var path = ConfigPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            var tmp = path + ".tmp-" + Environment.ProcessId;
            File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tmp, path, overwrite: true);
        }
    }

    // ─── Consent helpers ─────────────────────────────────────────────────────

    public bool IsConsentGranted() => DataEgressConsent;

    /// <summary>동의 flag + UTC timestamp 갱신 후 즉시 Save.</summary>
    public void GrantConsent()
    {
        DataEgressConsent = true;
        ConsentTimestampUtc = DateTime.UtcNow.ToString("o");
        Save();
        Log.Info($"LlmConsent granted — {ConfigPath}");
    }

    /// <summary>
    /// consent 가 없으면 사용자에게 opt-in 다이얼로그를 표시. true=granted (이미 또는 신규 동의).
    /// 거부 시 false. 메인 UI 스레드에서 호출.
    /// </summary>
    public static bool EnsureGranted()
    {
        var config = Load();
        if (config.IsConsentGranted()) return true;

        const string msg =
            "Promaker LLM Chat 사용 시 다음 정보가 외부 LLM 서비스 (Claude / OpenAI / Anthropic / Ollama) 로 전송됩니다:\n" +
            "  • 대화에 입력하는 사용자 메시지\n" +
            "  • LLM 이 read tool 로 조회한 모델 정보 (system / flow / work 이름, 구조)\n" +
            "  • Promaker 가 정의한 system prompt\n\n" +
            "전송 채널: Claude CLI / Codex CLI / Anthropic API / OpenAI API / Ollama (local).\n" +
            "API 키 / 비밀번호 / 파일 시스템 경로 등은 전송되지 않습니다.\n\n" +
            "동의하시겠습니까?\n\n" +
            "(거부 시 LLM Chat 기능이 차단됩니다. 추후 동의는 LLM Chat 메뉴 재진입 시 다시 묻습니다.)";

        var owner = Application.Current?.MainWindow;
        var result = owner != null
            ? MessageBox.Show(owner, msg, "LLM 데이터 전송 동의", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            : MessageBox.Show(msg, "LLM 데이터 전송 동의", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            config.GrantConsent();
            return true;
        }
        Log.Info("LlmConsent declined");
        return false;
    }

    // ─── API key helpers (DPAPI) ─────────────────────────────────────────────

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
            Log.Warn($"LlmConfig.GetApiKey({providerKey}) 복호화 실패: {ex.Message}");
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
