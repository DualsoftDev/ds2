using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Windows;
using log4net;
using Llm.Shared;
using Llm.Shared.Abstractions;
using Llm.Shared.Api;
using Llm.Shared.Mcp;
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
    // **D-2-1 / D-2-5 / D-2-6 SSOT** (done-lighthouse-kb-server.md §0):
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
    // **MR4 SSOT** (done-lighthouse-kb-index.md §3.15.5):
    //   - daily 한도 (default 10K token) → 초과 시 caption 생성 skip (NULL 유지, 다음 day 재시도)
    //   - soft warning 80% 도달 시 KbManagerDialog chip 안내 (UI 측 hook)
    //   - 색인 진입 전 confirm dialog (예상 token = image 수 × 평균 300 token 산정)
    //
    // DPAPI 미적용 평문 — daily reset 가 사용자 가시화 필요 (수동 reset / 한도 변경 시각 명확).
    // 다중 process race = 본 정책의 의도 외 (single Promaker instance 가정 충분, cli 는 자체 cost-aware caller 책임).

    [JsonPropertyName("visionCostGate")]
    public VisionCostGate VisionCostGate { get; set; } = new();

    // ─── LightHouse Service (done-lighthouse-kb-server.md §3.4 / §3.7 / Phase S5) ────────────────

    /// <summary>
    /// 등록된 KB collection 의 active 셋 (T1 flat). Promaker startup 시 1회 GET /collections 로 server registry sync.
    /// chat panel open 시 Active=true 인 entry 의 CollectionId 만 POST /sessions 의 collectionIds 로 전달.
    /// parent r5 SKIP 으로 본 schema 가 prod 최초 도입 (migration 부담 0).
    /// </summary>
    [JsonPropertyName("kbCollections")]
    public List<KbCollectionEntry> KbCollections { get; set; } = new();

    /// <summary>
    /// **D-S7-3a (s6-r29) — legacy singular field (backward-compat load only)**.
    /// <para/>
    /// 단수 시절 (s6-r28 이전) 의 disk JSON 호환 — <see cref="MigrateLegacyLightHouseService"/> 가 load 시점에
    /// 단수를 <see cref="LightHouseServices"/>[0] 로 변환 + null clear. Save 시 null = <c>JsonIgnore(WhenWritingNull)</c>
    /// 로 disk JSON 에서 자동 누락. 즉 새 caller 는 본 필드를 사용하지 말고 <see cref="LightHouseServices"/> +
    /// <see cref="EnsureActiveService"/> / <see cref="ClearActiveService"/> 사용.
    /// </summary>
    [JsonPropertyName("lightHouseService")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LightHouseServiceConfig? LightHouseService { get; set; }

    /// <summary>
    /// **D-S7-3a (s6-r29) — multi-service routing SSOT** (done-lighthouse-kb-server.md §3.16 D-S7-3).
    /// <para/>
    /// LightHouse service 의 N개 동시 보유. 각 entry 는 <see cref="LightHouseServiceConfig.ServiceId"/> guid v4 로
    /// 식별. <see cref="KbCollectionEntry.ServiceId"/> 가 본 list 의 ServiceId 를 참조하여 collection 의 소속 service
    /// 결정. <c>Active=true</c> 인 service 만 session 발급 대상 (UI 표시는 모두 노출).
    /// <para/>
    /// **migration**: 단수 시절 (s6-r28 이전) 의 <see cref="LightHouseService"/> 가 load 시 자동으로 본 list 의 [0]
    /// 으로 변환 (ServiceId 자동 발급 / DisplayName "기본 서비스" / Active=true). 동시에 기존 PSK ciphertext 는
    /// per-service entropy 로 재암호화.
    /// </summary>
    [JsonPropertyName("lightHouseServices")]
    public List<LightHouseServiceConfig> LightHouseServices { get; set; } = new();

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
            var cfg = JsonSerializer.Deserialize<LlmConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            }) ?? new LlmConfig();
            cfg.MigrateLegacyLightHouseService();
            return cfg;
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
    /// Promaker 다중 인스턴스 동시 save race 방지: in-process lock + cross-process file lock + atomic write
    /// (`.tmp-&lt;pid&gt;` + Move overwrite). 이전 LlmConsent.Save M2 정책 + LlmApiConfig.Save atomic 패턴 통합.
    /// <para/>
    /// **R8-M5 (s6-r76, external review backlog)** — 이전에는 in-process lock 만 잡아서 다중 Promaker
    /// instance 가 동시에 Save 호출 시 마지막 writer 가 silent overwrite. <see cref="ModifyWithLock"/> 의
    /// cross-process file lock 과 정합 박제 — 둘 다 <see cref="WithFileLock"/> helper 통과.
    /// <para/>
    /// 주의: caller 가 미리 disk 의 latest 를 reload 하지 않으면 read-modify-write race 회피 못 함
    /// (cross-process file lock 은 *write 시점* race 만 차단). read-modify-write 의무 caller 는
    /// <see cref="ModifyWithLock"/> 사용 권장.
    /// <para/>
    /// UI freeze risk: STA UI thread 가 Save() 호출 시 worst-case ~12.7s lock 경합 가능. 다른 instance 가
    /// 동시에 ModifyWithLock 보유 중인 경우 backoff retry 누적. UI critical path 는 <c>Task.Run</c> wrap 권고.
    /// </summary>
    public void Save() => WithFileLock(SaveTo);

    /// <summary>
    /// **R8-M5 (s6-r76)** — cross-process file lock + retry/backoff helper. Save / ModifyWithLock 공통 SSOT.
    /// <para/>
    /// lock 충돌 (IOException) 시 7회 exponential backoff + per-attempt jitter (0~base/2 random).
    /// base = 100/200/400/800/1600/3200/6400ms. 누적 worst ≈ 12.7s + jitter. 모두 실패 시 throw.
    /// jitter 는 thundering herd (동시 retry 충돌) 회피.
    /// </summary>
    /// <param name="action">file lock 안에서 수행할 작업. 인자 = EffectiveConfigPath.</param>
    private static void WithFileLock(Action<string> action)
    {
        const int maxAttempts = 7;
        var path = EffectiveConfigPath;
        var lockPath = LockPath;
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                using var lockStream = new FileStream(
                    lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                action(path);
                return;
            }
            catch (IOException)
            {
                if (attempt == maxAttempts - 1) throw;
                var baseDelay = 100 << attempt;
                var jitter = Random.Shared.Next(0, baseDelay / 2 + 1);
                Thread.Sleep(baseDelay + jitter);
            }
        }
        throw new IOException($"LlmConfig.WithFileLock: lock 획득 실패 ({maxAttempts}회 retry, path={lockPath})");
    }

    // ─── s6-r24 작업 2 (MJ1 해소) — cross-process atomic Load→mutate→Save ──────────────

    /// <summary>
    /// **test 전용** — ConfigPath override. Promaker.Tests 가 fact 별 임시 path 박제로 production
    /// `%APPDATA%\Dualsoft\Promaker\Settings\llm-config.json` 침투 회피.
    /// production code 는 null 그대로 (`EffectiveConfigPath = ConfigPath`).
    /// <para/>
    /// **process-static 정합 (s6-r25, m3)**: 본 property 는 process-wide 단일 상태. 동시에 여러 test
    /// class 가 set 하면 cross-class race 발생 — 모든 caller 는 같은 xUnit Collection
    /// (`[Collection("LlmConfigOverride")]`, `LlmConfigModifyWithLockTests` 박제) 에 묶어 직렬화 필수.
    /// 또한 `internal` 가시성이라 `Promaker.Tests` 외부 assembly 에서 사용 시 `[InternalsVisibleTo]`
    /// 추가 + 같은 collection 합류 의무. 위반 시 Save 가 다른 test 의 override path 로 직렬화되어
    /// collection 일부가 영구 누락.
    /// </summary>
    internal static string? TestConfigPathOverride { get; set; }

    private static string EffectiveConfigPath => TestConfigPathOverride ?? ConfigPath;

    /// <summary>cross-process file lock path. ConfigPath 와 sibling.</summary>
    private static string LockPath => EffectiveConfigPath + ".lock";

    /// <summary>
    /// **s6-r25 (M1) — stale lock 파일 best-effort cleanup**. `App.OnExit` 또는 `OnStartup` 가 호출.
    /// <para/>
    /// 동작: lock file 을 <c>FileShare.None</c> 으로 short-open 시도. 성공 = 다른 process 가 잡고 있지
    /// 않음 → delete. 실패 (IOException, 다른 Promaker instance 가 lock 보유 중) = swallow. lock file
    /// 잔재가 production user-visible artifact 라 uninstall / clean state 의무 — 그러나 기능 결함 0
    /// (다음 ModifyWithLock 호출이 OpenOrCreate 로 reuse).
    /// </summary>
    public static void TryCleanupStaleLockFile()
    {
        var lockPath = LockPath;
        if (!File.Exists(lockPath)) return;
        try
        {
            using (var stream = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                // 잡힘 — free 상태 확인됨.
            }
            File.Delete(lockPath);
        }
        catch (IOException) { /* 다른 instance 가 보유 중 — skip */ }
        catch (UnauthorizedAccessException) { /* ACL 결함 — skip */ }
    }

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
    /// <para>retry (s6-r25, m2 cap ↑): lock 충돌 (IOException) 시 7회 exponential backoff +
    /// per-attempt jitter (0~base/2 random). base = 100 / 200 / 400 / 800 / 1600 / 3200 / 6400ms.
    /// 누적 worst ≈ 12.7s + jitter. 이전 5회 (cap 3.1s) 는 CI 부하 + 4×50 trips contention 시 flaky 위험.
    /// jitter 는 thundering herd (동시 retry 충돌) 회피. 모두 실패 시 caller 에 throw — best-effort
    /// 컨텍스트 (예: KbManagerDialog finally) 는 catch 후 log 권장. STA UI thread 호출 시 worst-case
    /// freeze 회피 위해 `Task.Run` async wrap 권고 (s6-r25 자가 검열 review Minor-1).</para>
    /// </summary>
    /// <returns>Save 직후의 LlmConfig (caller 가 본 인스턴스 자체 state 갱신 시 활용).</returns>
    public static LlmConfig ModifyWithLock(Action<LlmConfig> mutate)
    {
        if (mutate is null) throw new ArgumentNullException(nameof(mutate));
        // R8-M5 — file lock + retry SSOT 는 WithFileLock helper. Save() 와 정합 박제.
        LlmConfig? result = null;
        WithFileLock(path =>
        {
            var cfg = LoadFrom(path);
            mutate(cfg);
            cfg.SaveTo(path);
            result = cfg;
        });
        return result!;
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
    /// **D-S7-3a (s6-r29) — per-service DPAPI entropy builder** (결정 #4 정합).
    /// <para/>
    /// serviceId 가 비어있으면 legacy entropy (<see cref="LightHousePskEntropy"/>) 반환 — migration 전 단수
    /// 시절 ciphertext 복호화 호환. 비어있지 않으면 <c>Promaker.LightHouseService.v1.{serviceId}</c> per-service
    /// entropy 반환 — service A 의 PSK ciphertext 가 service B 의 entropy 로 복호화 안 됨 (defense-in-depth).
    /// <para/>
    /// **silent contract 박제 (자가 검열 Minor-1, s6-r29 review)**: <c>BuildLightHousePskEntropy("")</c> ≡
    /// <see cref="LightHousePskEntropy"/> (byte-equal) 가 migration path 의 legacy ciphertext 복호화 invariant.
    /// 본 entropy 문자열 (`Promaker.LightHouseService.v1`) 의 v2 bump 시 legacy 호환 깨짐 — 단수 시절 사용자의
    /// 모든 PSK 가 ProtectedData.Unprotect 에서 throw → catch 분기의 ApiKeyEncrypted="" clear 진입 → 사용자
    /// 전체 재입력 의무. v 정정 시 반드시 별 turn 의 migration v2 path 신설 의무 (paired-release 검출 outside scope).
    /// </summary>
    private static byte[] BuildLightHousePskEntropy(string serviceId)
    {
        if (string.IsNullOrEmpty(serviceId)) return LightHousePskEntropy;
        return Encoding.UTF8.GetBytes($"Promaker.LightHouseService.v1.{serviceId}");
    }

    /// <summary>
    /// **D-S7-3a (s6-r29)** — 지정 service 의 PSK 평문 복호화. 미설정 / 복호화 실패 시 null.
    /// 호출자는 평문 PSK 의 메모리 잔존 최소화 위해 변수 lifetime 을 짧게 유지 (review S4-r1 IM-5 backlog).
    /// </summary>
    public string? GetLightHousePsk(string serviceId)
    {
        if (string.IsNullOrEmpty(serviceId)) return null;
        var svc = LightHouseServices.FirstOrDefault(s => s.ServiceId == serviceId);
        if (svc is null || string.IsNullOrEmpty(svc.ApiKeyEncrypted)) return null;
        try
        {
            var encrypted = Convert.FromBase64String(svc.ApiKeyEncrypted);
            var entropy = BuildLightHousePskEntropy(serviceId);
            var plain = ProtectedData.Unprotect(encrypted, entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            Log.Warn($"LlmConfig.GetLightHousePsk({serviceId}) 복호화 실패: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// **D-S7-3a (s6-r29) backward-compat overload** — active service 의 PSK 반환. 다음 phase (D-S7-3b/c)
    /// 에서 caller (Holder / Settings / ChatViewModel) 가 ServiceId 명시 path 로 마이그레이션 완료 시 제거 후보.
    /// </summary>
    public string? GetLightHousePsk()
    {
        var svc = LightHouseServices.FirstOrDefault(s => s.Active);
        return svc is null ? null : GetLightHousePsk(svc.ServiceId);
    }

    /// <summary>
    /// **D-S7-3a (s6-r29)** — 지정 service 의 ApiKeyEncrypted 갱신. null / 빈 입력 = 박제 제거.
    /// service entry 가 list 에 없으면 <see cref="InvalidOperationException"/>.
    /// <para/>**PR2 (2026-05-27) — deprecated**: SecureString overload 사용 권장. managed string path 는 heap 평문 GC 잔존 risk.
    /// </summary>
    [Obsolete("PR2 (2026-05-27): SetLightHousePsk(string, SecureString) overload 사용 권장. managed string path 는 heap 평문 GC 잔존 risk.")]
    public void SetLightHousePsk(string serviceId, string? psk)
    {
        if (string.IsNullOrEmpty(serviceId)) throw new ArgumentException("serviceId required", nameof(serviceId));
        var svc = LightHouseServices.FirstOrDefault(s => s.ServiceId == serviceId)
                  ?? throw new InvalidOperationException($"LightHouseServices entry not found: {serviceId}");
        if (string.IsNullOrEmpty(psk))
        {
            svc.ApiKeyEncrypted = "";
            return;
        }
        var plain = Encoding.UTF8.GetBytes(psk);
        try
        {
            var entropy = BuildLightHousePskEntropy(serviceId);
            var encrypted = ProtectedData.Protect(plain, entropy, DataProtectionScope.CurrentUser);
            svc.ApiKeyEncrypted = Convert.ToBase64String(encrypted);
        }
        finally
        {
            // **B5 (2026-05-27)** — plain byte[] wipe (best-effort). 단 string psk 는 immutable 이라 GC heap 잔존.
            Array.Clear(plain, 0, plain.Length);
        }
    }

    /// <summary>
    /// **B5 (2026-05-27) — SecureString 정공 overload**. heap 평문 lifetime 완전 제거 — caller (LightHouseLocalInstaller)
    /// 가 SecureString 직접 박제 → 본 메서드가 SecureString → byte[] (lock 보호) → DPAPI Protect → finally Array.Clear.
    /// <para/>
    /// caller 는 SecureString 의 소유권 유지 — 본 메서드가 Dispose 안 함 (caller 의 finally 책임). PSK 가 null/Length==0 시
    /// ApiKeyEncrypted 박제 제거 (string overload 와 contract 동일성).
    /// </summary>
    public void SetLightHousePsk(string serviceId, SecureString? psk)
    {
        if (string.IsNullOrEmpty(serviceId)) throw new ArgumentException("serviceId required", nameof(serviceId));
        var svc = LightHouseServices.FirstOrDefault(s => s.ServiceId == serviceId)
                  ?? throw new InvalidOperationException($"LightHouseServices entry not found: {serviceId}");
        if (psk is null || psk.Length == 0)
        {
            svc.ApiKeyEncrypted = "";
            return;
        }
        var plain = LightHouseLocalInstaller.SecureStringToUtf8Bytes(psk);
        try
        {
            var entropy = BuildLightHousePskEntropy(serviceId);
            var encrypted = ProtectedData.Protect(plain, entropy, DataProtectionScope.CurrentUser);
            svc.ApiKeyEncrypted = Convert.ToBase64String(encrypted);
        }
        finally
        {
            Array.Clear(plain, 0, plain.Length);
        }
    }

    // **B5 자가 검열 M1 fix (2026-05-27)** — SecureStringToUtf8Bytes 중복 제거. LightHouseLocalInstaller.SecureStringToUtf8Bytes
    // SSOT 사용 (internal helper). using Promaker.Services 박제됨 (line 16) — 의존 방향 동일.

    /// <summary>
    /// **D-S7-3a (s6-r29) backward-compat overload** — active service 가 있으면 PSK 갱신, 없으면 신규 service
    /// (Active=true) 생성. UI / Holder 가 D-S7-3c 진입 시 명시 ServiceId 사용으로 마이그레이션 후 제거 후보.
    /// <para/>**PR2 (2026-05-27) — deprecated**: SecureString overload 사용 권장.
    /// </summary>
    [Obsolete("PR2 (2026-05-27): SetLightHousePsk(SecureString) overload 사용 권장.")]
    public void SetLightHousePsk(string? psk)
    {
        var svc = EnsureActiveService();
#pragma warning disable CS0618  // 본 helper 가 string overload SSOT 경유 — internal 위임.
        SetLightHousePsk(svc.ServiceId, psk);
#pragma warning restore CS0618
    }

    public bool HasLightHousePsk() => !string.IsNullOrEmpty(GetLightHousePsk());

    // ─── D-S7-3a multi-service helpers ───────────────────────────────────────────

    /// <summary>
    /// **D-S7-3a (s6-r29)** — active service 보장. 없으면 신규 entry 생성 (ServiceId 자동 발급 + DisplayName
    /// "기본 서비스" + Active=true) 후 추가. caller (Settings dialog) 가 BaseUrl / PSK 입력 직전 호출.
    /// </summary>
    public LightHouseServiceConfig EnsureActiveService(string defaultDisplayName = "기본 서비스")
    {
        var svc = LightHouseServices.FirstOrDefault(s => s.Active);
        if (svc is not null) return svc;
        svc = new LightHouseServiceConfig
        {
            ServiceId = Guid.NewGuid().ToString(),
            DisplayName = defaultDisplayName,
            Active = true,
        };
        LightHouseServices.Add(svc);
        return svc;
    }

    /// <summary>
    /// **D-S7-3a (s6-r29)** — active service entry 제거 (BaseUrl 빈 입력 등 "서비스 해제" 시점). 해당 service
    /// 소속 collection 들의 ServiceId 는 *그대로 보존* — 사용자가 다시 동일 service 등록 시 ServiceId 재매핑
    /// 의무는 별 turn (D-S7-3c UI) 박제.
    /// </summary>
    public void ClearActiveService()
    {
        var svc = LightHouseServices.FirstOrDefault(s => s.Active);
        if (svc is not null) LightHouseServices.Remove(svc);
    }

    /// <summary>
    /// **D-S7-3a (s6-r29) — backward-compat migration**. load 시점에 호출.
    /// <para/>
    /// 단수 시절 disk JSON 에 <c>lightHouseService</c> 가 있고 복수 <c>lightHouseServices</c> 가 비어 있으면,
    /// 단수 entry 를 복수[0] 으로 자동 변환:
    /// <list type="bullet">
    /// <item>ServiceId 자동 발급 (Guid v4) — 결정 #2.</item>
    /// <item>DisplayName 미지정 시 "기본 서비스" 채움.</item>
    /// <item>Active=true 강제 — 단수 시절 service 는 default active 가정.</item>
    /// <item>ApiKeyEncrypted (legacy entropy 로 암호화) 를 복호화 후 per-service entropy 로 재암호화. 복호화 실패
    /// 시 빈 문자열 clear (사용자 재입력 안내).</item>
    /// <item>KbCollections 의 ServiceId 가 빈 entry 는 새 ServiceId 로 채움 — 단수 시절 collection 은 모두 본
    /// service 소속.</item>
    /// </list>
    /// 변환 완료 후 <see cref="LightHouseService"/> 는 null clear → 다음 Save 시 <c>JsonIgnore(WhenWritingNull)</c>
    /// 로 disk JSON 에서 자동 누락.
    /// <para/>
    /// **단수 + 복수 동시 박제 시**: 복수 우선, 단수 무시 (warn 로그 + null clear). 결정 #1 (자동 변환 조용히)
    /// 정합 — 사용자 dialog 진입 0.
    /// </summary>
    internal void MigrateLegacyLightHouseService()
    {
        if (LightHouseService is null) return;
        if (LightHouseServices.Count > 0)
        {
            Log.Warn("LlmConfig — lightHouseService(단수) + lightHouseServices(복수) 동시 박제. 복수 우선, 단수 무시.");
            // **자가 검열 Major-1 적용 (s6-r29 review)** — coexist 분기에서 단수의 ApiKeyEncrypted 가 비어있지
            // 않으면 사용자 PSK 가 silent 폐기됨 → 정보 손실 알림 1줄 추가. disk JSON 백업 후 복원 권고.
            if (!string.IsNullOrEmpty(LightHouseService.ApiKeyEncrypted))
            {
                Log.Warn("LlmConfig — coexist 분기에서 단수 lightHouseService.ApiKeyEncrypted 가 폐기됨. disk JSON 백업 후 복원 권고.");
            }
            LightHouseService = null;
            return;
        }

        var migrated = LightHouseService;
        if (string.IsNullOrEmpty(migrated.ServiceId)) migrated.ServiceId = Guid.NewGuid().ToString();
        if (string.IsNullOrEmpty(migrated.DisplayName)) migrated.DisplayName = "기본 서비스";
        migrated.Active = true;

        if (!string.IsNullOrEmpty(migrated.ApiKeyEncrypted))
        {
            try
            {
                var legacyBytes = Convert.FromBase64String(migrated.ApiKeyEncrypted);
                var plain = ProtectedData.Unprotect(legacyBytes, LightHousePskEntropy, DataProtectionScope.CurrentUser);
                var newEntropy = BuildLightHousePskEntropy(migrated.ServiceId);
                var reencrypted = ProtectedData.Protect(plain, newEntropy, DataProtectionScope.CurrentUser);
                migrated.ApiKeyEncrypted = Convert.ToBase64String(reencrypted);
            }
            catch (Exception ex)
            {
                Log.Warn($"LlmConfig migration — legacy PSK 재암호화 실패 ({ex.Message}). 사용자 재입력 필요.");
                migrated.ApiKeyEncrypted = "";
            }
        }

        LightHouseServices.Add(migrated);

        foreach (var c in KbCollections)
        {
            if (string.IsNullOrEmpty(c.ServiceId)) c.ServiceId = migrated.ServiceId;
        }

        LightHouseService = null;
        Log.Info($"LlmConfig — lightHouseService(단수) → lightHouseServices[0] 자동 변환 (ServiceId={migrated.ServiceId}).");
    }

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
        // DPAPI fallback 은 Windows-only (CurrentUser scope, ProtectedData). WPF Promaker 는 Windows 한정이라
        // 통상 redundant 이나, PromakerLlmAgent assembly 가 cross-platform host 에 reuse 되는 향후 use case 가드.
        // env var 미박제 + 비-Windows = silent null + warn (caller 가 SkippedCaption 분기 진입).
        if (!OperatingSystem.IsWindows())
        {
            Log.Warn("GetVlmApiKey: LIGHTHOUSE_VLM_API_KEY env var 미박제 + Windows 외 OS — DPAPI fallback 불가, null 반환.");
            return null;
        }
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

    /// <summary>
    /// 오늘 누적 token (caller 가 caption 호출 직후 추정 token 으로 증분).
    /// <para/>
    /// thread-safe 보호 — read/write 모두 <see cref="_gateLock"/> 하에 직렬화 (s6-r25 M1, 자가 검열
    /// review Major-1 정합). property setter 도 lock 안에서 호출되어야 정합.
    /// </summary>
    [JsonPropertyName("tokensUsedToday")]
    public int TokensUsedToday { get; set; }

    /// <summary>
    /// **thread-safe rollover + Consume 직렬화 lock** (s6-r25, 자가 검열 review Major-1 정합).
    /// <para/>
    /// 이전 `Interlocked.Add` 만으로는 day-rollover 경계에서 race — RolloverIfNeeded 의
    /// `TokensUsedToday = 0` setter 가 동시 Add 결과를 덮어쓰기 가능. 본 lock 으로 reset + accumulate
    /// 를 단일 critical section 으로 묶음. private field 라 System.Text.Json 직렬화 자연 제외.
    /// </summary>
    private readonly object _gateLock = new();

    /// <summary>
    /// UTC 기준 day rollover 처리. caller 가 매 caption 호출 직전 호출 의무 — 본 메서드가 disk save 는 하지 않음
    /// (caller 가 cluster 호출 후 1회 save 책임).
    /// </summary>
    public void RolloverIfNeeded()
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        lock (_gateLock)
        {
            if (LastResetUtc != today)
            {
                LastResetUtc = today;
                TokensUsedToday = 0;
            }
        }
    }

    /// <summary>요청 token 만큼 한도가 남는지 (hard cutoff 검사). caller 가 호출 직전 사용.</summary>
    public bool CanAfford(int requestedTokens)
    {
        lock (_gateLock)
        {
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            if (LastResetUtc != today)
            {
                LastResetUtc = today;
                TokensUsedToday = 0;
            }
            return TokensUsedToday + requestedTokens <= DailyTokenCap;
        }
    }

    /// <summary>
    /// caption 호출 성공 시 누적. caller 책임 — Indexer 측 captionGen wrapper 에서 호출.
    /// <para/>
    /// **thread-safe 누적 + rollover (s6-r25 M1 + 자가 검열 review Major-1)**: 단일 critical section
    /// 안에서 rollover check + reset + accumulate. Phase 4 perf (parallel extractor) 진입 시 의무 —
    /// 단일 thread Indexer 가정 폐기 가드.
    /// </summary>
    public void Consume(int tokens)
    {
        lock (_gateLock)
        {
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            if (LastResetUtc != today)
            {
                LastResetUtc = today;
                TokensUsedToday = 0;
            }
            TokensUsedToday += tokens;
        }
    }

    /// <summary>
    /// UI chip 안내용 — 80% 도달 시 warning, hard cap 시 hard.
    /// <para/>
    /// **side-effect free (s6-r25 m3)**: 본 getter 는 pure — 이전 RolloverIfNeeded 호출 제거. caller 는
    /// 정확한 status 가 필요한 시점에 `RolloverIfNeeded()` 또는 `CanAfford` / `Consume` (둘 다 내부에서
    /// rollover 호출) 을 명시적으로 선행 호출할 의무. day rollover 가 일어나지 않은 채 어제 token
    /// 누적이 그대로 read 되는 risk 회피.
    /// </summary>
    public VisionCostGateStatus Status
    {
        get
        {
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
/// LlmConfig.KbCollections 의 entry (done-lighthouse-kb-server.md §3.4).
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

    /// <summary>
    /// **D-S7-3a (s6-r29)** — 본 collection 의 소속 LightHouse service ID (<see cref="LightHouseServiceConfig.ServiceId"/>).
    /// migration 시 단수 lightHouseService 시절 collection 은 모두 단수 service 의 새 ServiceId 로 채워짐.
    /// </summary>
    [JsonPropertyName("serviceId")]
    public string ServiceId { get; set; } = "";
}

/// <summary>
/// LightHouse Service 연결 정보 (done-lighthouse-kb-server.md §3.4 / §3.7 / §3.16 D-S7-3).
///
/// BaseUrl = HTTPS-only (plain HTTP 거부, §3.7). 사내 service URL (e.g. https://service.company.local:8443).
/// ApiKeyEncrypted = DPAPI(CurrentUser) base64 of PSK. 평문 ApiKey 키 사용 금지 (CR4).
/// LightHouseClient 가 매 요청 시 <see cref="LlmConfig.GetLightHousePsk(string)"/> 로 복호화 + Authorization: Bearer 동봉.
/// <para/>
/// **D-S7-3a (s6-r29) — multi-service routing 확장**: 단일 LlmConfig 가 여러 LightHouse service 를 동시 보유.
/// <list type="bullet">
/// <item><c>ServiceId</c> = client 자체 발급 guid v4 (결정 D-S7-3 #2). per-service DPAPI entropy 격리 키.</item>
/// <item><c>DisplayName</c> = KbManagerDialog tab / Settings dialog row 의 사용자 표시명.</item>
/// <item><c>Active</c> = "session 발급 대상" 단일 의미 (결정 D-S7-3 #3). UI 표시는 active 여부와 무관 (inactive 도 표시, dim 처리).</item>
/// </list>
/// </summary>
public sealed class LightHouseServiceConfig
{
    /// <summary>**D-S7-3a (s6-r29)** — client 가 발급한 guid v4. per-service DPAPI entropy 키. migration 시 자동 발급.</summary>
    [JsonPropertyName("serviceId")]
    public string ServiceId { get; set; } = "";

    /// <summary>**D-S7-3a (s6-r29)** — UI 표시명. migration 시 단수 entry 는 "기본 서비스" 로 채움.</summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "";

    [JsonPropertyName("apiKeyEncrypted")]
    public string ApiKeyEncrypted { get; set; } = "";

    /// <summary>**D-S7-3a (s6-r29)** — session 발급 대상 여부 (결정 #3). default true (신규 생성 시 즉시 사용 의도).</summary>
    [JsonPropertyName("active")]
    public bool Active { get; set; } = true;

    /// <summary>
    /// **B5 D-S7-1 후속 (s6-r61)** — mTLS client cert thumbprint (SHA-1 또는 SHA-256, hex, ':'/공백/hyphen 허용).
    /// <para/>
    /// nullable / 빈 값 시 mTLS 미적용 (PSK 단독 인증, 현행). 박제 시 LightHouseClient 가 LocalMachine\My X509Store
    /// 에서 cert 찾기 + HttpClientHandler.ClientCertificates 박제. cert 미존재 시 client 측 fail-fast.
    /// <para/>
    /// **발급/관리 정책** (사용자 박제 의무):
    ///   - 사내 CA 발급 cert → LocalMachine\My import (PowerShell `Import-PfxCertificate` 또는 MMC certificate snap-in).
    ///   - 사용자 manual 입력 (cert subject / thumbprint copy) — UI X509Store 선택 dialog 는 별 turn (B5 phase 2).
    ///   - DPAPI 미적용 — thumbprint 는 식별자 (평문 정합), 실 cert private key 는 X509Store 가 OS-level protect.
    /// <para/>
    /// **server-side (D-S7-1 s6-r53 박제)**: `config.json` 의 `mtls.mode = "optional"|"required"` + 선택
    /// `allowedThumbprints` whitelist. mode="required" 시 cert 미박제 = TLS handshake 거부.
    /// </summary>
    [JsonPropertyName("clientCertThumbprint")]
    public string? ClientCertThumbprint { get; set; }

    /// <summary>
    /// **Phase 4 P4-C.2 (s6-r38)** — 본 service 안 색인 시 사용할 embedding backend 박제.
    /// <para/>
    /// nullable — 미박제 / Enabled=false 시 BM25-only fallback. 추천안 정합 (사용자 결정):
    /// service 별 nested 박제 (multi-service routing 정합 — service 마다 다른 backend 가능). collection 별
    /// override 는 backlog (cost 큼).
    /// <para/>
    /// Ollama 는 unauth (localhost) 라 API key 박제 0 → DPAPI 적용 skip. 향후 OpenAI / Anthropic embedding API
    /// 확장 시 별도 ApiKeyEncrypted 필드 추가 + 기존 LightHouseServiceConfig.ApiKeyEncrypted DPAPI 패턴 적용.
    /// </summary>
    [JsonPropertyName("embedding")]
    public EmbeddingProviderConfig? Embedding { get; set; }
}

/// <summary>
/// **Phase 4 P4-C.2 (s6-r38)** — embedding backend 설정 SSOT (Ollama 만 지원, 추천안 정합).
/// <para/>
/// `Enabled=false` 또는 본 record null 시 BM25-only fallback (Phase 1 동작). `Enabled=true` 시 caller
/// (AttachmentIngestService) 가 `OllamaEmbedder(BaseUrl, Model, Dimension)` 생성 후 색인 시점 주입.
/// <para/>
/// **dimension SSOT**: sqlite-vec 의 `vec0(embedding float[N])` 와 정합 의무. lib 의
/// `SqliteStore.EmbeddingDimension = 1024` (bge-m3 기준). 사용자가 다른 모델 (예: nomic-embed-text = 768)
/// 박제 시 lib schema 의 vec0 dim 동반 변경 필요 — 통상 사용자 결정 (사용자 manual / FAQ 박제).
/// 본 turn 에서는 dim mismatch 시 backend 호출 결과 검증 단계 (`OllamaEmbedder.GenerateAsync`) 에서
/// `InvalidOperationException` fail-fast.
/// </summary>
public sealed class EmbeddingProviderConfig
{
    /// <summary>embedder 활성화 여부. false 시 BM25-only fallback (Phase 1 동작 정합).</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    /// <summary>Ollama daemon base URL. default "http://localhost:11434" (OllamaDefaults 정합).</summary>
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>embedding model 이름. default "bge-m3" (1024 dim, sqlite-vec schema 정합).</summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = "bge-m3";

    /// <summary>vector dimension. lib 의 SqliteStore.EmbeddingDimension 과 정합 의무. default 1024 (bge-m3).</summary>
    [JsonPropertyName("dimension")]
    public int Dimension { get; set; } = 1024;
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
