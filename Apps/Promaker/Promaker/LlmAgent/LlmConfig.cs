using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Windows;
using log4net;
using Promaker.LlmAgent.Api;
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
    /// <summary>
    /// LightHouse Service PSK 의 DPAPI entropy. LLM provider key 의 <see cref="DpapiEntropy"/> 와 분리 —
    /// 의미가 다르고 (LAN service 인증 vs 외부 LLM provider) leak 시 영향 범위도 다름. 별 entropy 로 영구 격리.
    /// </summary>
    private static readonly byte[] LightHousePskEntropy = Encoding.UTF8.GetBytes("Promaker.LightHouseService.v1");

    public static string ConfigPath => SettingsPaths.Of("llm-config.json");

    // ─── Consent ─────────────────────────────────────────────────────────────

    [JsonPropertyName("dataEgressConsent")]
    public bool DataEgressConsent { get; set; }

    [JsonPropertyName("consentTimestampUtc")]
    public string? ConsentTimestampUtc { get; set; }

    /// <summary>
    /// Codex CLI 추가 동의. Codex 는 0.125 기준 danger-full-access sandbox 만 MCP tool call 허용 →
    /// 일반 LLM 동의보다 강한 권한 위임 (file system / network 자유 접근). 임시 워크스페이스 cd 격리가
    /// 1차 방어이지만, 사용자에게 명시적 추가 동의 받음.
    /// </summary>
    [JsonPropertyName("codexConsentGranted")]
    public bool CodexConsentGranted { get; set; }

    [JsonPropertyName("codexConsentTimestampUtc")]
    public string? CodexConsentTimestampUtc { get; set; }

    /// <summary>
    /// API 모드 (Anthropic / OpenAI) 의 토큰 과금 위험에 대한 사용자 인지 동의. CLI provider (Claude / Codex)
    /// 는 사용자가 별도 구독하므로 본 flag 와 무관. Ollama 는 local 이라 비용 없음.
    /// 한 번 동의하면 provider 별 재확인 없이 모든 API 모드 진입 허용.
    /// </summary>
    [JsonPropertyName("apiCostConsentGranted")]
    public bool ApiCostConsentGranted { get; set; }

    [JsonPropertyName("apiCostConsentTimestampUtc")]
    public string? ApiCostConsentTimestampUtc { get; set; }

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

    // ─── VLM (Vision Language Model) — Phase 2 task D (s6-r20) ────────────────────────────────────
    //
    // **D-2-1 / D-2-5 / D-2-6 SSOT** (todo-lighthouse-kb-server.md §0):
    //   - 1차 default = Anthropic + Sonnet 4.6 (parent CR2 정합)
    //   - client-side 색인 시점 호출 (eager at indexing time, D-2-2)
    //   - VLM API key = 본 LlmConfig 의 EncryptedKeys (DPAPI per-user) 재활용
    //     → 별 키 격리 안 함 — Anthropic API key 가 chat + VLM 양쪽 공용 (사용자 관점 단순 + cost 분리는 console 측)
    //   - lighthouse-cli 무인 batch path = LIGHTHOUSE_VLM_API_KEY env var fallback (Phase S6 P5)
    //
    // 평문 필드 (Provider / Model) — DPAPI 미적용. 모델 식별자는 비밀 아님.

    /// <summary>
    /// VLM provider 식별자 — 현재 "anthropic" 만 지원 (D-2-1 결정). 향후 "openai" 추가 가능하지만 본 phase 미박제.
    /// 빈 문자열 또는 "none" = VLM 비활성 (CaptionGenerator.noop 사용 — Phase 1 회귀 0).
    /// </summary>
    [JsonPropertyName("vlmProvider")]
    public string VlmProvider { get; set; } = "anthropic";

    /// <summary>
    /// VLM 모델 ID. anthropic 인 경우 messages API 의 `model` 필드 — default "claude-sonnet-4-6" (D-2-1 cost 균형).
    /// "claude-opus-4-7" escalation 은 Phase 4 (사용자 명시 "더 정밀하게" path) — 본 phase default 아님.
    /// </summary>
    [JsonPropertyName("vlmModel")]
    public string VlmModel { get; set; } = "claude-sonnet-4-6";

    // ─── Vision Cost Gate — Phase 2 task E (s6-r20, MR4 정합) ─────────────────────────────────────
    //
    // **MR4 SSOT** (todo-lighthouse-kb-index.md §3.15.5):
    //   - daily 한도 (default 10K token) → 초과 시 caption 생성 skip (NULL 유지, 다음 day 재시도)
    //   - soft warning 80% 도달 시 KbManagerDialog chip 안내 (UI 측 hook)
    //   - 색인 진입 전 confirm dialog (예상 token = image 수 × 평균 300 token 산정)
    //
    // DPAPI 미적용 평문 — daily reset 가 사용자 가시화 필요 (수동 reset / 한도 변경 시각 명확).
    // 다중 process race = 본 정책의 의도 외 (single Promaker instance 가정 충분, cli 는 자체 cost-aware caller 책임).

    [JsonPropertyName("visionCostGate")]
    public VisionCostGate VisionCostGate { get; set; } = new();

    // ─── LightHouse Service (todo-lighthouse-kb-server.md §3.4 / §3.7 / Phase S5) ────────────────

    /// <summary>
    /// 등록된 KB collection 의 active 셋 (T1 flat). Promaker startup 시 1회 GET /collections 로 server registry sync.
    /// chat panel open 시 Active=true 인 entry 의 CollectionId 만 POST /sessions 의 collectionIds 로 전달.
    /// parent r5 SKIP 으로 본 schema 가 prod 최초 도입 (migration 부담 0).
    /// </summary>
    [JsonPropertyName("kbCollections")]
    public List<KbCollectionEntry> KbCollections { get; set; } = new();

    /// <summary>
    /// LightHouse Service 엔드포인트 + 인증 정보 (DPAPI 암호화 PSK). null = service 미설정.
    /// ApplicationSettingsDialog "LightHouse Service" section 에서 사용자가 BaseUrl + PSK 입력 시 즉시 채워짐.
    /// </summary>
    [JsonPropertyName("lightHouseService")]
    public LightHouseServiceConfig? LightHouseService { get; set; }

    // ─── I/O ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Corrupt JSON 시 LLM Chat 영구 차단 회피: `.bak` 백업 후 default 반환.
    /// 다음 Save 시 새 정상 파일이 작성되어 사용자가 동의 다이얼로그를 다시 거치게 됨 (이전 LlmConsent.cs M2 정책 보존).
    /// </summary>
    public static LlmConfig Load() => LoadFrom(ConfigPath);

    /// <summary>
    /// 명시 path 로 부터 load (테스트 / 마이그레이션 전용). production code 는 <see cref="Load"/>.
    /// </summary>
    internal static LlmConfig LoadFrom(string path)
    {
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

    // ─── s6-r24 작업 2 (MJ1 해소) — cross-process atomic Load→mutate→Save ──────────────

    /// <summary>
    /// **test 전용** — ConfigPath override. Promaker.Tests 가 fact 별 임시 path 박제로 production
    /// `%APPDATA%\Dualsoft\Promaker\Settings\llm-config.json` 침투 회피.
    /// production code 는 null 그대로 (`EffectiveConfigPath = ConfigPath`).
    /// </summary>
    internal static string? TestConfigPathOverride { get; set; }

    private static string EffectiveConfigPath => TestConfigPathOverride ?? ConfigPath;

    /// <summary>cross-process file lock path. ConfigPath 와 sibling.</summary>
    private static string LockPath => EffectiveConfigPath + ".lock";

    /// <summary>
    /// **s6-r24 작업 2 — MJ1 (multi-instance read-modify-write race) 본질 해결**.
    ///
    /// <para>Promaker 다중 인스턴스가 동시에 `Load → modify → Save` 시 stale snapshot 덮어쓰기 차단.
    /// cross-process file lock (lock 파일을 <c>FileShare.None</c> 으로 open 유지) 으로 critical section 직렬화.</para>
    ///
    /// <para>호출 시 disk 의 최신 LlmConfig 를 reload → caller 의 <paramref name="mutate"/> 호출 →
    /// 즉시 Save. mutate 콜백 안에서는 다른 process 의 mutation 결과를 본 인스턴스의 값으로 *덮어쓰기 안 함*
    /// (caller 책임 = delta 누적 또는 max 선택). cap 같이 disk SSOT 인 필드는 본 인스턴스 값 무시.</para>
    ///
    /// <para>retry: lock 충돌 (IOException) 시 100ms / 200 / 400 / 800 / 1600ms exponential backoff
    /// 5회. 모두 실패 시 caller 에 throw — best-effort 컨텍스트 (예: KbManagerDialog finally) 는 catch 후
    /// log 권장.</para>
    /// </summary>
    /// <returns>Save 직후의 LlmConfig (caller 가 본 인스턴스 자체 state 갱신 시 활용).</returns>
    public static LlmConfig ModifyWithLock(Action<LlmConfig> mutate)
    {
        if (mutate is null) throw new ArgumentNullException(nameof(mutate));
        const int maxAttempts = 5;
        var path = EffectiveConfigPath;
        var lockPath = LockPath;
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                using var lockStream = new FileStream(
                    lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var cfg = LoadFrom(path);
                mutate(cfg);
                cfg.SaveTo(path);
                return cfg;
            }
            catch (IOException)
            {
                if (attempt == maxAttempts - 1) throw;
                Thread.Sleep(100 << attempt);  // 100 / 200 / 400 / 800 / 1600 ms
            }
        }
        throw new IOException($"LlmConfig.ModifyWithLock: lock 획득 실패 ({maxAttempts}회 retry, path={lockPath})");
    }

    /// <summary>**test 전용** — 명시 path 로 atomic write. <see cref="Save"/> 의 path override 형식.</summary>
    internal void SaveTo(string path)
    {
        lock (_saveLock)
        {
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
            "Promaker LLM Chat 사용 시 다음 정보가 외부 LLM 서비스 (Claude / OpenAI / Anthropic / Ollama / Groq) 로 전송됩니다:\n" +
            "  • 대화에 입력하는 사용자 메시지\n" +
            "  • LLM 이 read tool 로 조회한 모델 정보 (system / flow / work 이름, 구조)\n" +
            "  • Promaker 가 정의한 system prompt\n" +
            "  • 사용자가 첨부한 파일 — 텍스트/코드 (prompt 본문에 fenced block 으로 inline) /\n" +
            "    이미지·PDF (multimodal content block 으로 base64 또는 임시 파일 spool 후 전송)\n\n" +
            "전송 채널: Claude CLI / Codex CLI / Anthropic API / OpenAI API / Ollama (local) / Groq API.\n" +
            "API 키 / 비밀번호 / 파일 시스템 경로 등은 전송되지 않습니다.\n" +
            "비밀정보 보관 파일 (.env 등) 은 첨부 자체가 차단됩니다.\n\n" +
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

    // ─── Codex consent (extra) ───────────────────────────────────────────────

    public bool IsCodexConsentGranted() => CodexConsentGranted;

    public void GrantCodexConsent()
    {
        CodexConsentGranted = true;
        CodexConsentTimestampUtc = DateTime.UtcNow.ToString("o");
        Save();
        Log.Info($"Codex consent granted — {ConfigPath}");
    }

    /// <summary>
    /// Codex provider 첫 선택 시 별도 동의 다이얼로그. true=granted, false=거부 (provider 비활성화).
    /// 일반 LLM 동의 (<see cref="EnsureGranted"/>) 이미 있어야 호출 가능. 메인 UI 스레드에서 호출.
    /// </summary>
    public static bool EnsureCodexConsent()
    {
        var config = Load();
        if (config.IsCodexConsentGranted()) return true;

        const string msg =
            "Codex CLI 사용 시 일반 LLM 동의에 더해 다음 추가 권한이 위임됩니다:\n" +
            "  • Codex 0.125 는 sandbox_mode = \"danger-full-access\" 에서만 MCP tool call 을 통과시킴\n" +
            "    (read-only / workspace-write 모드에서는 MCP 호출이 자동 cancel — community issue 1379772)\n" +
            "  • danger-full-access 는 file system / network 자유 접근 허용\n" +
            "  • Promaker 는 임시 빈 폴더 (cd:) 격리로 1차 완화하지만 OS 레벨 sandbox 는 미적용\n" +
            "  • Codex 가 자체 판단으로 file system 탐색을 시도할 수 있음 — system prompt 의 refusal 이 차선책\n" +
            "  • Codex 의 thread rollout 이 사용자 ~/.codex/sessions/<sid>.jsonl 에 평문 누적\n\n" +
            "Codex 사용에 동의하시겠습니까?\n\n" +
            "(거부 시 Codex provider 가 비활성화됩니다. Claude / API providers 는 그대로 사용 가능.)";

        var owner = System.Windows.Application.Current?.MainWindow;
        var result = owner != null
            ? System.Windows.MessageBox.Show(owner, msg, "Codex 추가 권한 동의", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning)
            : System.Windows.MessageBox.Show(msg, "Codex 추가 권한 동의", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);

        if (result == System.Windows.MessageBoxResult.Yes)
        {
            config.GrantCodexConsent();
            return true;
        }
        Log.Info("Codex consent declined");
        return false;
    }

    // ─── API cost consent (Anthropic / OpenAI) ──────────────────────────────

    public bool IsApiCostConsentGranted() => ApiCostConsentGranted;

    public void GrantApiCostConsent()
    {
        ApiCostConsentGranted = true;
        ApiCostConsentTimestampUtc = DateTime.UtcNow.ToString("o");
        Save();
        Log.Info($"API cost consent granted — {ConfigPath}");
    }

    /// <summary>
    /// API 모드 (Anthropic API / OpenAI API) 첫 선택 시 토큰 과금 경고 다이얼로그. true=granted (이미 또는 신규),
    /// false=거부 (provider 비활성화). 메인 UI 스레드에서 호출.
    /// </summary>
    public static bool EnsureApiCostConsent(string providerLabel)
    {
        var config = Load();
        if (config.IsApiCostConsentGranted()) return true;

        var msg =
            $"{providerLabel} 는 외부 LLM 서비스의 유료 API 입니다. 사용 시 다음 사항을 인지해 주세요:\n\n" +
            "  • 매 요청마다 입력/출력 토큰량에 따라 사용자 계정으로 과금됩니다\n" +
            "  • Promaker 의 system prompt + tool 정의 + 모델 snapshot 이 매 turn 함께 전송되어\n" +
            "    짧은 사용자 입력에도 수천~수만 토큰이 송신될 수 있습니다\n" +
            "  • 첨부 파일 (이미지/PDF/텍스트) 은 토큰 사용량을 크게 증가시킵니다\n" +
            "  • Promaker 는 사용량 / 비용 한도를 제어하지 않으며 책임지지 않습니다\n" +
            "    — provider 콘솔 (Anthropic Console / OpenAI Platform) 에서 직접 예산/한도 설정 필요\n\n" +
            "API 모드 사용에 동의하시겠습니까?\n\n" +
            "(거부 시 본 provider 가 비활성화됩니다. Claude CLI / Codex CLI / Ollama (local) 는 그대로 사용 가능.)";

        var owner = Application.Current?.MainWindow;
        var result = owner != null
            ? MessageBox.Show(owner, msg, "API 비용 경고", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            : MessageBox.Show(msg, "API 비용 경고", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            config.GrantApiCostConsent();
            return true;
        }
        Log.Info("API cost consent declined");
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

    // ─── LightHouse PSK helpers (DPAPI / CurrentUser) ─────────────────────────────

    /// <summary>
    /// `LightHouseService.ApiKeyEncrypted` 를 DPAPI 로 복호화한 평문 PSK 반환. 미설정 / 복호화 실패 시 null.
    /// 호출자 (LightHouseClient) 는 매 요청마다 평문 PSK 를 헤더에 박지만, 메모리 잔존 최소화를 위해
    /// 변수 lifetime 을 짧게 유지할 것 (review S4-r1 IM-5 backlog).
    /// </summary>
    public string? GetLightHousePsk()
    {
        if (LightHouseService is null || string.IsNullOrEmpty(LightHouseService.ApiKeyEncrypted))
            return null;
        try
        {
            var encrypted = Convert.FromBase64String(LightHouseService.ApiKeyEncrypted);
            var plain = ProtectedData.Unprotect(encrypted, LightHousePskEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            Log.Warn($"LlmConfig.GetLightHousePsk 복호화 실패: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 평문 PSK 를 DPAPI 로 암호화 후 `LightHouseService.ApiKeyEncrypted` 박제. null / 빈 입력 시 박제 제거.
    /// LightHouseService 가 null 이면 신규 생성.
    /// </summary>
    public void SetLightHousePsk(string? psk)
    {
        LightHouseService ??= new LightHouseServiceConfig();
        if (string.IsNullOrEmpty(psk))
        {
            LightHouseService.ApiKeyEncrypted = "";
            return;
        }
        var plain = Encoding.UTF8.GetBytes(psk);
        var encrypted = ProtectedData.Protect(plain, LightHousePskEntropy, DataProtectionScope.CurrentUser);
        LightHouseService.ApiKeyEncrypted = Convert.ToBase64String(encrypted);
    }

    public bool HasLightHousePsk() => !string.IsNullOrEmpty(GetLightHousePsk());

    // ─── VLM helpers (Phase 2 task D, s6-r20) ───────────────────────────────────

    /// <summary>
    /// VLM 활성 여부. provider 가 "anthropic" 이면서 해당 provider API key 가 박제 된 경우만 true.
    /// Phase 4 등에서 provider 확장 시 본 메서드의 분기 갱신 의무 (현재 anthropic 만).
    /// </summary>
    public bool IsVlmEnabled()
    {
        if (string.IsNullOrWhiteSpace(VlmProvider)) return false;
        var prov = VlmProvider.Trim().ToLowerInvariant();
        if (prov == "none" || prov == "off") return false;
        if (prov == "anthropic") return HasApiKey(ApiProviderFactory.AnthropicKey);
        return false;
    }

    /// <summary>
    /// 활성 VLM provider 의 API key 반환. anthropic 인 경우 EncryptedKeys["anthropic"] (DPAPI 복호화).
    /// LIGHTHOUSE_VLM_API_KEY env var 가 박제 되어 있으면 env 가 우선 — lighthouse-cli 무인 batch path 정합.
    /// 미설정 시 null.
    ///
    /// **--review M2 정합 (s6-r20)** — 기존 `ApiProviderFactory_AnthropicKey` const mirror 제거 + Api 네임스페이스
    /// 직접 참조. 같은 root namespace (Promaker.LlmAgent) 안이라 의존 사이클 없음 — SSOT 단일화.
    /// </summary>
    public string? GetVlmApiKey()
    {
        var envKey = Environment.GetEnvironmentVariable("LIGHTHOUSE_VLM_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey)) return envKey;
        if (string.IsNullOrWhiteSpace(VlmProvider)) return null;
        var prov = VlmProvider.Trim().ToLowerInvariant();
        if (prov == "anthropic") return GetApiKey(ApiProviderFactory.AnthropicKey);
        return null;
    }
}

/// <summary>
/// Vision Cost Gate (MR4) — daily token cap 박제. Phase 2 task E (s6-r20).
///
/// 정책: caller (`AttachmentIngestService`) 가 색인 진입 전 본 클래스의 `EnsureBudgetAsync` 호출하여
/// 예상 token (image 수 × 300) 가 daily cap 안에 들어오는지 확인. 80% 도달 시 KbManagerDialog chip 갱신.
/// hard cutoff 시 captionGen 이 SkippedCaption 반환 (caller 가 별도 진입 차단 안 함 — per-image granularity).
///
/// LastResetUtc 가 오늘 (UTC) 과 다르면 자동 reset (TokensUsedToday=0 + LastResetUtc=오늘 자정).
/// </summary>
public sealed class VisionCostGate
{
    [JsonPropertyName("dailyTokenCap")]
    public int DailyTokenCap { get; set; } = 10_000;

    /// <summary>마지막 reset (UTC date, ISO-8601 yyyy-MM-dd). 빈 문자열 = 아직 reset 한 적 없음.</summary>
    [JsonPropertyName("lastResetUtc")]
    public string LastResetUtc { get; set; } = "";

    /// <summary>오늘 누적 token (caller 가 caption 호출 직후 추정 token 으로 증분).</summary>
    [JsonPropertyName("tokensUsedToday")]
    public int TokensUsedToday { get; set; }

    /// <summary>
    /// UTC 기준 day rollover 처리. caller 가 매 caption 호출 직전 호출 의무 — 본 메서드가 disk save 는 하지 않음
    /// (caller 가 cluster 호출 후 1회 save 책임).
    /// </summary>
    public void RolloverIfNeeded()
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        if (LastResetUtc != today)
        {
            LastResetUtc = today;
            TokensUsedToday = 0;
        }
    }

    /// <summary>요청 token 만큼 한도가 남는지 (hard cutoff 검사). caller 가 호출 직전 사용.</summary>
    public bool CanAfford(int requestedTokens)
    {
        RolloverIfNeeded();
        return TokensUsedToday + requestedTokens <= DailyTokenCap;
    }

    /// <summary>caption 호출 성공 시 누적. caller 책임 — Indexer 측 captionGen wrapper 에서 호출.</summary>
    public void Consume(int tokens)
    {
        RolloverIfNeeded();
        TokensUsedToday += tokens;
    }

    /// <summary>UI chip 안내용 — 80% 도달 시 warning, hard cap 시 hard.</summary>
    public VisionCostGateStatus Status
    {
        get
        {
            RolloverIfNeeded();
            if (DailyTokenCap <= 0) return VisionCostGateStatus.Disabled;
            if (TokensUsedToday >= DailyTokenCap) return VisionCostGateStatus.HardCap;
            // --review m4 정합 — `* 100` / `* 80` int 곱 overflow 차단 (DailyTokenCap ≥ ~21M 일 때 발생).
            // long cast 로 안전. 정확도 손실 없음 (정수 비교).
            if ((long)TokensUsedToday * 100 >= (long)DailyTokenCap * 80) return VisionCostGateStatus.SoftWarning;
            return VisionCostGateStatus.Normal;
        }
    }

    /// <summary>예상 token (image 수 × 평균 300 token / image) — 색인 진입 confirm dialog 의 표시값 산정 SSOT.</summary>
    [JsonIgnore]
    public const int AverageTokensPerImage = 300;

    public static int EstimateTokens(int imageCount) => imageCount * AverageTokensPerImage;
}

public enum VisionCostGateStatus
{
    Disabled,
    Normal,
    SoftWarning,
    HardCap,
}

/// <summary>
/// LlmConfig.KbCollections 의 entry (todo-lighthouse-kb-server.md §3.4).
///
/// CollectionId = server 가 첫 POST /collections 응답에 발급한 guid v4 (D3).
/// DisplayName = 사용자 표시 이름 (사용자가 KbManagerDialog 에 입력. server 의 displayName 과 정합 유지).
/// Active = chat panel open 시 ATTACH 대상 여부. T1 flat 이라 모든 사용자가 토글 가능.
/// </summary>
public sealed class KbCollectionEntry
{
    [JsonPropertyName("collectionId")]
    public string CollectionId { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("active")]
    public bool Active { get; set; }
}

/// <summary>
/// LightHouse Service 연결 정보 (todo-lighthouse-kb-server.md §3.4 / §3.7).
///
/// BaseUrl = HTTPS-only (plain HTTP 거부, §3.7). 사내 service URL (e.g. https://service.company.local:8443).
/// ApiKeyEncrypted = DPAPI(CurrentUser) base64 of PSK. 평문 ApiKey 키 사용 금지 (CR4).
/// LightHouseClient 가 매 요청 시 GetLightHousePsk() 로 복호화 + Authorization: Bearer 동봉.
/// </summary>
public sealed class LightHouseServiceConfig
{
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "";

    [JsonPropertyName("apiKeyEncrypted")]
    public string ApiKeyEncrypted { get; set; } = "";
}

/// <summary>
/// 사용자가 LLM provider 진입 시 동의 다이얼로그에서 "거부" 를 선택한 경우 throw. 정상 흐름 (사용자 의도) 이므로
/// <c>ConfigureProviderAsync</c> catch 가 일반 Exception 분기 (Log.Error + 에러 톤) 가 아닌 별도 분기로
/// Info 레벨 + 안내 메시지로 처리한다.
/// </summary>
public sealed class LlmProviderDeclinedException : Exception
{
    public LlmProviderDeclinedException(string message) : base(message) { }
}
