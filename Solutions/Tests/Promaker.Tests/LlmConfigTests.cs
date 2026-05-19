using System;
using System.IO;
using System.Security.Cryptography;
using Promaker.LlmAgent;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// Phase 2 후속 — LlmConfig (consent + provider 통합) 회귀 테스트.
/// 핵심: corrupt JSON → .bak 백업 + default 반환, DPAPI key 라운드트립, atomic write.
/// </summary>
public sealed class LlmConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Promaker.Tests",
        nameof(LlmConfigTests),
        Guid.NewGuid().ToString("N"));

    public LlmConfigTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // ─── Corrupt JSON → fallback ─────────────────────────────────────────────

    [Fact]
    public void LoadFrom_returns_default_when_file_missing()
    {
        var path = Path.Combine(_root, "missing.json");
        var cfg = LlmConfig.LoadFrom(path);

        Assert.NotNull(cfg);
        Assert.False(cfg.DataEgressConsent);
        Assert.Equal("Claude", cfg.DefaultProvider);
    }

    [Fact]
    public void LoadFrom_corrupt_JSON_creates_bak_and_returns_default()
    {
        var path = Path.Combine(_root, "corrupt.json");
        File.WriteAllText(path, "{ this is not valid json ##");

        var cfg = LlmConfig.LoadFrom(path);

        // default 반환
        Assert.False(cfg.DataEgressConsent);
        Assert.Equal("Claude", cfg.DefaultProvider);

        // .bak 으로 corrupt 파일 보존
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(path + ".bak"));
    }

    [Fact]
    public void LoadFrom_valid_JSON_round_trips_all_fields()
    {
        var path = Path.Combine(_root, "valid.json");
        const string json = """
            {
              "dataEgressConsent": true,
              "consentTimestampUtc": "2026-05-07T12:00:00Z",
              "defaultProvider": "Codex",
              "encryptedKeys": { "anthropic": "BASE64HERE" },
              "anthropicModel": "claude-opus-4-7",
              "openAiModel": "gpt-5",
              "ollamaModel": "qwen2.5",
              "ollamaBaseUrl": "http://192.168.1.50:11434"
            }
            """;
        File.WriteAllText(path, json);

        var cfg = LlmConfig.LoadFrom(path);

        Assert.True(cfg.DataEgressConsent);
        Assert.Equal("2026-05-07T12:00:00Z", cfg.ConsentTimestampUtc);
        Assert.Equal("Codex", cfg.DefaultProvider);
        Assert.Equal("BASE64HERE", cfg.EncryptedKeys["anthropic"]);
        Assert.Equal("claude-opus-4-7", cfg.AnthropicModel);
        Assert.Equal("gpt-5", cfg.OpenAiModel);
        Assert.Equal("qwen2.5", cfg.OllamaModel);
        Assert.Equal("http://192.168.1.50:11434", cfg.OllamaBaseUrl);
    }

    [Fact]
    public void LoadFrom_unknown_extra_fields_are_ignored()
    {
        // Deserialize 에 PropertyNameCaseInsensitive=true 만 적용 — 알 수 없는 field 는 silently skip
        var path = Path.Combine(_root, "extra.json");
        File.WriteAllText(path, """{"dataEgressConsent":true,"unknownFutureField":42}""");

        var cfg = LlmConfig.LoadFrom(path);
        Assert.True(cfg.DataEgressConsent);
    }

    // ─── DPAPI key set/get round-trip ────────────────────────────────────────

    [Fact]
    public void SetApiKey_then_GetApiKey_returns_same_plaintext()
    {
        if (!OperatingSystem.IsWindows()) return; // DPAPI Windows 전용

        var cfg = new LlmConfig();
        const string plain = "sk-ant-api03-fake-test-key-1234567890";

        cfg.SetApiKey("anthropic", plain);
        var got = cfg.GetApiKey("anthropic");

        Assert.Equal(plain, got);
        Assert.True(cfg.HasApiKey("anthropic"));

        // EncryptedKeys 안에는 base64 + 암호화 형태 — 평문 일치 X
        Assert.Single(cfg.EncryptedKeys);
        Assert.NotEqual(plain, cfg.EncryptedKeys["anthropic"]);
    }

    [Fact]
    public void SetApiKey_with_null_or_empty_removes_key()
    {
        if (!OperatingSystem.IsWindows()) return;

        var cfg = new LlmConfig();
        cfg.SetApiKey("openai", "sk-temp");
        Assert.True(cfg.HasApiKey("openai"));

        cfg.SetApiKey("openai", null);
        Assert.False(cfg.HasApiKey("openai"));
        Assert.False(cfg.EncryptedKeys.ContainsKey("openai"));
    }

    [Fact]
    public void GetApiKey_for_missing_provider_returns_null()
    {
        var cfg = new LlmConfig();
        Assert.Null(cfg.GetApiKey("nonexistent"));
        Assert.False(cfg.HasApiKey("nonexistent"));
    }

    // ─── Phase S5a — KbCollections + LightHouseService ───────────────────────

    [Fact]
    public void KbCollections_round_trips_through_save_load()
    {
        var path = Path.Combine(_root, "kb-roundtrip.json");
        const string json = """
            {
              "dataEgressConsent": true,
              "kbCollections": [
                {"collectionId":"550e8400-e29b-41d4-a716-446655440000","displayName":"라인A","active":true},
                {"collectionId":"77777777-e29b-41d4-a716-446655440000","displayName":"라인B","active":false}
              ]
            }
            """;
        File.WriteAllText(path, json);

        var cfg = LlmConfig.LoadFrom(path);
        Assert.Equal(2, cfg.KbCollections.Count);
        Assert.Equal("550e8400-e29b-41d4-a716-446655440000", cfg.KbCollections[0].CollectionId);
        Assert.Equal("라인A", cfg.KbCollections[0].DisplayName);
        Assert.True(cfg.KbCollections[0].Active);
        Assert.False(cfg.KbCollections[1].Active);
    }

    [Fact]
    public void Legacy_singular_lightHouseService_migrates_to_plural_on_load()
    {
        // **D-S7-3a (s6-r29)** — 단수 시절 (s6-r28 이전) disk JSON 의 `lightHouseService` 가 load 시 자동으로
        // `LightHouseServices`[0] 으로 변환되고 단수 필드는 null clear. ServiceId 자동 발급 (Guid v4).
        // DisplayName "기본 서비스" 채움. Active=true 강제. (ApiKeyEncrypted 비어 있어 PSK 재암호화 path 미진입 — 본 fact 는 schema 변환만 검증.)
        var path = Path.Combine(_root, "lhs-roundtrip.json");
        const string json = """
            {
              "lightHouseService": {
                "baseUrl": "https://service.test.local:8443",
                "apiKeyEncrypted": ""
              }
            }
            """;
        File.WriteAllText(path, json);

        var cfg = LlmConfig.LoadFrom(path);

        // migration 완료 후 단수 필드 null clear (다음 Save 시 disk JSON 에서 누락).
        Assert.Null(cfg.LightHouseService);
        // 복수 [0] 으로 변환됨 — BaseUrl 그대로 + ServiceId 자동 발급 + DisplayName "기본 서비스" + Active=true.
        Assert.Single(cfg.LightHouseServices);
        var migrated = cfg.LightHouseServices[0];
        Assert.Equal("https://service.test.local:8443", migrated.BaseUrl);
        Assert.Equal("", migrated.ApiKeyEncrypted);
        Assert.False(string.IsNullOrEmpty(migrated.ServiceId));
        Assert.True(Guid.TryParse(migrated.ServiceId, out _));
        Assert.Equal("기본 서비스", migrated.DisplayName);
        Assert.True(migrated.Active);
    }

    [Fact]
    public void Legacy_PSK_reencryption_succeeds_on_migration()
    {
        // **D-S7-3a (s6-r29)** — 단수 시절 legacy entropy 로 실제 DPAPI 암호화된 PSK 가 disk JSON 에 박제된
        // 경우, migration 시 자동으로 per-service entropy 로 재암호화. GetLightHousePsk(serviceId) 로 복호화 성공.
        if (!OperatingSystem.IsWindows()) return;

        // 1단계: 단수 시절 disk 형태를 모의 — legacy LightHousePskEntropy 로 PSK 암호화 → base64 ApiKeyEncrypted 산출.
        const string plain = "legacy-psk-1234567890";
        var legacyEntropy = System.Text.Encoding.UTF8.GetBytes("Promaker.LightHouseService.v1");
        var legacyCipher = ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(plain), legacyEntropy, DataProtectionScope.CurrentUser);
        var legacyBase64 = Convert.ToBase64String(legacyCipher);

        var path = Path.Combine(_root, "lhs-legacy-psk.json");
        var json = $$"""
            {
              "lightHouseService": {
                "baseUrl": "https://legacy.local:8443",
                "apiKeyEncrypted": "{{legacyBase64}}"
              }
            }
            """;
        File.WriteAllText(path, json);

        // 2단계: Load → migration. legacy ciphertext 가 per-service entropy 로 재암호화됨.
        var cfg = LlmConfig.LoadFrom(path);
        Assert.Single(cfg.LightHouseServices);
        var migrated = cfg.LightHouseServices[0];
        Assert.False(string.IsNullOrEmpty(migrated.ApiKeyEncrypted));
        Assert.NotEqual(legacyBase64, migrated.ApiKeyEncrypted);

        // 3단계: 복호화 성공 — 평문 그대로 복구.
        Assert.Equal(plain, cfg.GetLightHousePsk(migrated.ServiceId));
    }

    [Fact]
    public void Legacy_PSK_reencryption_failure_clears_ciphertext()
    {
        // **D-S7-3a (s6-r29)** — legacy entropy 로 복호화 실패한 ciphertext 는 migration 시 catch 분기에서
        // 빈 문자열로 clear (사용자 재입력 안내 의도). 사용자의 dialog 진입 0 + 정상 흐름 보장.
        var path = Path.Combine(_root, "lhs-legacy-bad-psk.json");
        const string json = """
            {
              "lightHouseService": {
                "baseUrl": "https://corrupt.local:8443",
                "apiKeyEncrypted": "ZmFrZS1lbmNyeXB0ZWQtYmFzZTY0"
              }
            }
            """;
        File.WriteAllText(path, json);

        var cfg = LlmConfig.LoadFrom(path);
        Assert.Single(cfg.LightHouseServices);
        Assert.Equal("", cfg.LightHouseServices[0].ApiKeyEncrypted);
        // BaseUrl / ServiceId / DisplayName 는 그대로 보존 (사용자가 PSK 만 재입력하면 즉시 사용 가능).
        Assert.Equal("https://corrupt.local:8443", cfg.LightHouseServices[0].BaseUrl);
    }

    [Fact]
    public void Legacy_migration_fills_KbCollections_ServiceId()
    {
        // **D-S7-3a (s6-r29)** — 단수 시절 disk JSON 의 KbCollections 는 ServiceId 빈 값 (필드 자체가 없음).
        // migration 시 새 service ServiceId 로 일괄 채움.
        var path = Path.Combine(_root, "kb-legacy-migration.json");
        const string json = """
            {
              "kbCollections": [
                { "collectionId": "c1", "displayName": "Doc1", "active": true },
                { "collectionId": "c2", "displayName": "Doc2", "active": false }
              ],
              "lightHouseService": {
                "baseUrl": "https://svc.local:8443",
                "apiKeyEncrypted": ""
              }
            }
            """;
        File.WriteAllText(path, json);

        var cfg = LlmConfig.LoadFrom(path);

        Assert.Single(cfg.LightHouseServices);
        var newServiceId = cfg.LightHouseServices[0].ServiceId;
        Assert.Equal(2, cfg.KbCollections.Count);
        Assert.All(cfg.KbCollections, k => Assert.Equal(newServiceId, k.ServiceId));
    }

    [Fact]
    public void Plural_and_singular_coexist_plural_wins()
    {
        // **D-S7-3a (s6-r29)** — 동시 박제 시 복수 우선, 단수 무시 (warn 로그 + null clear). 결정 #1 (자동 변환 조용히).
        // **자가 검열 Minor-4 (s6-r29 review)** — 단수의 ApiKeyEncrypted 가 복수 entry 로 *흡수되지 않음* 검증
        // (silent 폐기). [Major-1] log 1줄 추가는 별 fact 검증 안 함 (log assertion 미도입 — 사용자 식별 가능 한도).
        var path = Path.Combine(_root, "lhs-both.json");
        const string json = """
            {
              "lightHouseService": {
                "baseUrl": "https://legacy.local:8443",
                "apiKeyEncrypted": "TEdBQ1k="
              },
              "lightHouseServices": [
                {
                  "serviceId": "11111111-1111-1111-1111-111111111111",
                  "displayName": "Existing",
                  "baseUrl": "https://existing.local:8443",
                  "apiKeyEncrypted": "RVhJU1RJTkc=",
                  "active": true
                }
              ]
            }
            """;
        File.WriteAllText(path, json);

        var cfg = LlmConfig.LoadFrom(path);

        Assert.Null(cfg.LightHouseService);
        Assert.Single(cfg.LightHouseServices);
        Assert.Equal("https://existing.local:8443", cfg.LightHouseServices[0].BaseUrl);
        Assert.Equal("11111111-1111-1111-1111-111111111111", cfg.LightHouseServices[0].ServiceId);

        // 단수의 ApiKeyEncrypted ("TEdBQ1k=") 가 복수[0] 로 흡수되지 않음 — 기존 복수 entry 의 값 그대로.
        Assert.Equal("RVhJU1RJTkc=", cfg.LightHouseServices[0].ApiKeyEncrypted);
        // 추가 entry 도 생성되지 않음 — coexist 분기는 단수 폐기만 수행.
        Assert.DoesNotContain(cfg.LightHouseServices, s => s.BaseUrl == "https://legacy.local:8443");
    }

    [Fact]
    public void Per_service_entropy_empty_serviceId_equals_legacy_entropy_invariant()
    {
        // **자가 검열 Minor-1 (s6-r29 review)** — `BuildLightHousePskEntropy("")` ≡ legacy entropy 의 byte-equal
        // invariant 가 silent contract. 본 fact 는 invariant 의 *동작 검증* 으로 보호 (entropy 문자열 v2 bump
        // 시 본 fact 가 실패하여 migration v2 path 신설 의무를 강제).
        if (!OperatingSystem.IsWindows()) return;

        // legacy entropy 로 직접 암호화 (BuildLightHousePskEntropy 가 internal 이라 외부 reflection 대신 동일
        // 문자열 reproduce — 본 invariant 자체가 BuildLightHousePskEntropy("") == "Promaker.LightHouseService.v1"
        // 인 것을 검증).
        const string plain = "entropy-invariant-test";
        var legacyEntropy = System.Text.Encoding.UTF8.GetBytes("Promaker.LightHouseService.v1");
        var legacyCipher = ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(plain), legacyEntropy, DataProtectionScope.CurrentUser);
        var legacyBase64 = Convert.ToBase64String(legacyCipher);

        // legacy ciphertext 가 MigrateLegacyLightHouseService 의 catch path 진입하지 않고 복호화 성공 → 평문 복구.
        // = invariant 가 유지된다는 e2e 검증.
        var path = Path.Combine(_root, "entropy-invariant.json");
        var json = $$"""
            {
              "lightHouseService": {
                "baseUrl": "https://test.local:8443",
                "apiKeyEncrypted": "{{legacyBase64}}"
              }
            }
            """;
        File.WriteAllText(path, json);

        var cfg = LlmConfig.LoadFrom(path);
        var migrated = cfg.LightHouseServices[0];
        // catch path 진입 시 ApiKeyEncrypted="" — invariant 깨진 신호.
        Assert.NotEqual("", migrated.ApiKeyEncrypted);
        // 재암호화된 ciphertext 가 per-service entropy 로 복호화 성공.
        Assert.Equal(plain, cfg.GetLightHousePsk(migrated.ServiceId));
    }

    [Fact]
    public void Plural_lightHouseServices_round_trip_through_save_load()
    {
        // **D-S7-3a (s6-r29)** — 복수 entry 박제 + Save → Reload 정합. Save JSON 에 단수 lightHouseService 키 없음
        // (JsonIgnore(WhenWritingNull) + migration 후 LightHouseService=null).
        var path = Path.Combine(_root, "lhs-plural.json");
        var cfg = new LlmConfig();
        cfg.LightHouseServices.Add(new LightHouseServiceConfig
        {
            ServiceId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            DisplayName = "사내 본사",
            BaseUrl = "https://hq.company.local:8443",
            ApiKeyEncrypted = "QQ==",
            Active = true,
        });
        cfg.LightHouseServices.Add(new LightHouseServiceConfig
        {
            ServiceId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            DisplayName = "지사",
            BaseUrl = "https://branch.company.local:8443",
            ApiKeyEncrypted = "Qg==",
            Active = false,
        });
        cfg.SaveTo(path);

        var raw = File.ReadAllText(path);
        Assert.DoesNotContain("\"lightHouseService\":", raw);

        var reloaded = LlmConfig.LoadFrom(path);
        Assert.Equal(2, reloaded.LightHouseServices.Count);
        Assert.Equal("https://hq.company.local:8443", reloaded.LightHouseServices[0].BaseUrl);
        Assert.True(reloaded.LightHouseServices[0].Active);
        Assert.Equal("지사", reloaded.LightHouseServices[1].DisplayName);
        Assert.False(reloaded.LightHouseServices[1].Active);
    }

    [Fact]
    public void Default_LlmConfig_has_empty_collections_and_no_services()
    {
        var cfg = new LlmConfig();
        Assert.NotNull(cfg.KbCollections);
        Assert.Empty(cfg.KbCollections);
        Assert.Null(cfg.LightHouseService);
        Assert.NotNull(cfg.LightHouseServices);
        Assert.Empty(cfg.LightHouseServices);
        Assert.False(cfg.HasLightHousePsk());
    }

    [Fact]
    public void SetLightHousePsk_then_GetLightHousePsk_returns_same_plaintext()
    {
        if (!OperatingSystem.IsWindows()) return;

        var cfg = new LlmConfig();
        const string plain = "lighthouse-psk-test-1234567890";

        // **D-S7-3a (s6-r29) backward-compat overload** — EnsureActiveService 가 신규 entry 생성 후 PSK 저장.
        cfg.SetLightHousePsk(plain);
        Assert.True(cfg.HasLightHousePsk());
        Assert.Equal(plain, cfg.GetLightHousePsk());

        // 단수 필드는 사용 안 함. 복수 [0] 에 박제.
        Assert.Null(cfg.LightHouseService);
        Assert.Single(cfg.LightHouseServices);
        Assert.True(cfg.LightHouseServices[0].Active);
        Assert.NotEqual(plain, cfg.LightHouseServices[0].ApiKeyEncrypted);
    }

    [Fact]
    public void SetLightHousePsk_with_null_clears_encrypted()
    {
        if (!OperatingSystem.IsWindows()) return;

        var cfg = new LlmConfig();
        cfg.SetLightHousePsk("temp");
        Assert.True(cfg.HasLightHousePsk());

        cfg.SetLightHousePsk(null);
        Assert.False(cfg.HasLightHousePsk());
        Assert.Equal("", cfg.LightHouseServices[0].ApiKeyEncrypted);
    }

    [Fact]
    public void LightHousePsk_uses_distinct_entropy_from_LlmApi_keys()
    {
        // 다른 entropy 사용 검증 — LlmApi key 와 LightHouse PSK ciphertext 가 동일 평문이라도 별 entropy 라 다른 byte.
        if (!OperatingSystem.IsWindows()) return;

        var cfg1 = new LlmConfig();
        cfg1.SetApiKey("anthropic", "same-plain");
        cfg1.SetLightHousePsk("same-plain");

        Assert.NotEqual(cfg1.EncryptedKeys["anthropic"], cfg1.LightHouseServices[0].ApiKeyEncrypted);
    }

    [Fact]
    public void Per_service_PSK_entropy_isolation()
    {
        // **D-S7-3a (s6-r29) — 결정 #4 (per-service entropy)** — 동일 평문을 두 service 에 박제하면 ciphertext
        // 가 서로 다름. service A 의 ciphertext 를 service B 의 ServiceId 로 복호화 시도 시 null (실패).
        if (!OperatingSystem.IsWindows()) return;

        var cfg = new LlmConfig();
        var svcA = new LightHouseServiceConfig
        {
            ServiceId = Guid.NewGuid().ToString(),
            DisplayName = "A",
            BaseUrl = "https://a.local:8443",
            Active = true,
        };
        var svcB = new LightHouseServiceConfig
        {
            ServiceId = Guid.NewGuid().ToString(),
            DisplayName = "B",
            BaseUrl = "https://b.local:8443",
            Active = false,
        };
        cfg.LightHouseServices.Add(svcA);
        cfg.LightHouseServices.Add(svcB);

        const string plain = "same-psk-for-both";
        cfg.SetLightHousePsk(svcA.ServiceId, plain);
        cfg.SetLightHousePsk(svcB.ServiceId, plain);

        // 동일 평문이지만 ciphertext 다름 (entropy 분리 결과).
        Assert.NotEqual(svcA.ApiKeyEncrypted, svcB.ApiKeyEncrypted);

        // 각 service 의 ServiceId 로 복호화하면 동일 평문 복구.
        Assert.Equal(plain, cfg.GetLightHousePsk(svcA.ServiceId));
        Assert.Equal(plain, cfg.GetLightHousePsk(svcB.ServiceId));

        // service A 의 ciphertext 를 B 의 ServiceId 로 복호화 시도 — entropy 불일치로 null.
        // (ciphertext 강제 swap 시뮬레이션 — DPAPI Unprotect 가 entropy mismatch 시 throw → catch 후 null 반환.)
        svcB.ApiKeyEncrypted = svcA.ApiKeyEncrypted;
        Assert.Null(cfg.GetLightHousePsk(svcB.ServiceId));
    }

    [Fact]
    public void GetLightHousePsk_with_unknown_serviceId_returns_null()
    {
        var cfg = new LlmConfig();
        Assert.Null(cfg.GetLightHousePsk("nonexistent-id"));
        Assert.Null(cfg.GetLightHousePsk(""));
    }

    [Fact]
    public void SetLightHousePsk_with_unknown_serviceId_throws()
    {
        var cfg = new LlmConfig();
        Assert.Throws<InvalidOperationException>(() => cfg.SetLightHousePsk("nonexistent-id", "psk"));
        Assert.Throws<ArgumentException>(() => cfg.SetLightHousePsk("", "psk"));
    }

    [Fact]
    public void EnsureActiveService_creates_entry_when_none_active()
    {
        var cfg = new LlmConfig();
        Assert.Empty(cfg.LightHouseServices);

        var svc = cfg.EnsureActiveService("Custom Name");

        Assert.Single(cfg.LightHouseServices);
        Assert.Same(svc, cfg.LightHouseServices[0]);
        Assert.True(svc.Active);
        Assert.False(string.IsNullOrEmpty(svc.ServiceId));
        Assert.Equal("Custom Name", svc.DisplayName);

        // 두번째 호출 시 동일 entry 반환 (신규 생성 아님).
        var svc2 = cfg.EnsureActiveService();
        Assert.Same(svc, svc2);
        Assert.Single(cfg.LightHouseServices);
    }

    [Fact]
    public void ClearActiveService_removes_active_entry()
    {
        var cfg = new LlmConfig();
        cfg.EnsureActiveService();
        Assert.Single(cfg.LightHouseServices);

        cfg.ClearActiveService();
        Assert.Empty(cfg.LightHouseServices);

        // idempotent — 빈 상태에서 호출해도 throw 안 함.
        cfg.ClearActiveService();
        Assert.Empty(cfg.LightHouseServices);
    }

    // ─── Phase 2 task D-iii / E-i (s6-r20) — VLM provider/model + VisionCostGate ─────────────

    [Fact]
    public void Vlm_defaults_anthropic_sonnet_4_6()
    {
        var cfg = new LlmConfig();
        Assert.Equal("anthropic", cfg.VlmProvider);
        Assert.Equal("claude-sonnet-4-6", cfg.VlmModel);
        Assert.NotNull(cfg.VisionCostGate);
        Assert.Equal(10_000, cfg.VisionCostGate.DailyTokenCap);
        Assert.Equal(0, cfg.VisionCostGate.TokensUsedToday);
    }

    [Fact]
    public void Vlm_fields_round_trip_through_save_load()
    {
        var path = Path.Combine(_root, "vlm-roundtrip.json");
        const string json = """
            {
              "vlmProvider": "none",
              "vlmModel": "claude-opus-4-7",
              "visionCostGate": {
                "dailyTokenCap": 50000,
                "lastResetUtc": "2026-05-18",
                "tokensUsedToday": 1234
              }
            }
            """;
        File.WriteAllText(path, json);

        var cfg = LlmConfig.LoadFrom(path);
        Assert.Equal("none", cfg.VlmProvider);
        Assert.Equal("claude-opus-4-7", cfg.VlmModel);
        Assert.Equal(50000, cfg.VisionCostGate.DailyTokenCap);
        Assert.Equal("2026-05-18", cfg.VisionCostGate.LastResetUtc);
        Assert.Equal(1234, cfg.VisionCostGate.TokensUsedToday);
    }

    [Fact]
    public void IsVlmEnabled_requires_provider_and_apikey()
    {
        if (!OperatingSystem.IsWindows()) return;

        var cfg = new LlmConfig();
        // no apikey → false
        Assert.False(cfg.IsVlmEnabled());

        cfg.SetApiKey("anthropic", "sk-vlm-fake");
        Assert.True(cfg.IsVlmEnabled());

        cfg.VlmProvider = "none";
        Assert.False(cfg.IsVlmEnabled());
    }

    [Fact]
    public void GetVlmApiKey_env_var_takes_precedence()
    {
        if (!OperatingSystem.IsWindows()) return;

        const string envVar = "LIGHTHOUSE_VLM_API_KEY";
        var prior = Environment.GetEnvironmentVariable(envVar);
        try
        {
            Environment.SetEnvironmentVariable(envVar, "env-override-key");
            var cfg = new LlmConfig();
            cfg.SetApiKey("anthropic", "config-key");
            Assert.Equal("env-override-key", cfg.GetVlmApiKey());

            // env clear → config 값 fallback
            Environment.SetEnvironmentVariable(envVar, null);
            Assert.Equal("config-key", cfg.GetVlmApiKey());
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, prior);
        }
    }

    [Fact]
    public void VisionCostGate_CanAfford_respects_daily_cap()
    {
        var gate = new VisionCostGate { DailyTokenCap = 1000 };
        Assert.True(gate.CanAfford(500));
        gate.Consume(500);
        Assert.True(gate.CanAfford(500));   // 정확히 한도까지
        gate.Consume(500);
        Assert.False(gate.CanAfford(1));    // 초과
    }

    [Fact]
    public void VisionCostGate_Status_thresholds()
    {
        var gate = new VisionCostGate { DailyTokenCap = 1000 };
        Assert.Equal(VisionCostGateStatus.Normal, gate.Status);
        gate.Consume(799);
        Assert.Equal(VisionCostGateStatus.Normal, gate.Status);
        gate.Consume(1);    // 800 / 1000 = 80%
        Assert.Equal(VisionCostGateStatus.SoftWarning, gate.Status);
        gate.Consume(199);
        Assert.Equal(VisionCostGateStatus.SoftWarning, gate.Status);
        gate.Consume(1);    // 1000 도달
        Assert.Equal(VisionCostGateStatus.HardCap, gate.Status);
    }

    [Fact]
    public void VisionCostGate_RolloverIfNeeded_resets_on_new_day()
    {
        var gate = new VisionCostGate
        {
            DailyTokenCap = 1000,
            TokensUsedToday = 950,
            LastResetUtc = "2020-01-01",   // 과거 날짜
        };
        gate.RolloverIfNeeded();
        Assert.Equal(0, gate.TokensUsedToday);
        Assert.Equal(DateTime.UtcNow.ToString("yyyy-MM-dd"), gate.LastResetUtc);
    }

    [Fact]
    public void VisionCostGate_EstimateTokens_uses_average_300_per_image()
    {
        Assert.Equal(0, VisionCostGate.EstimateTokens(0));
        Assert.Equal(300, VisionCostGate.EstimateTokens(1));
        Assert.Equal(3000, VisionCostGate.EstimateTokens(10));
    }
}
