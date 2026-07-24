// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace DSPilot.Services;

/// <summary>
/// 데모용 관리자 게이트 + 데모 프리젠테이션 설정.
/// /demo/admin 관리 페이지에서 admin 로그인 후 다음을 제어한다(데모 전환이 마스터 스위치):
///   - Enabled(데모 전환): ON 이면 로그인 게이트 활성 + 사이드바 외부 바로가기 노출. OFF 면 순정 설치처럼 동작.
///   - LoginScope: 로그인 요구 범위 — "settings"(설정 페이지만) | "app"(첫 화면부터 전체).
///   - AdminId / 비밀번호: 관리자 자격 증명(비밀번호는 PBKDF2 해시로만 저장, 기본 admin/2747).
///   - Shortcuts: 설비박사·ReverseAI 바로가기의 개별 노출 여부·라벨·URL.
/// 설정은 AppSettings 와 분리된 마커/JSON 파일(demo-admin.json)로 영속 — GET /api/settings 스냅샷에 나타나지 않는다.
/// 관리 API(config/credentials)는 로그인 세션이 있어야만 상태를 노출한다(로그인 전에는 비노출 유지).
/// 세션 토큰은 인메모리 전용 — 서버 재시작 시 전부 무효화(재로그인 필요). 데모 용도로 충분.
/// </summary>
public class DemoAdminService
{
    private const string DefaultAdminId = "admin";
    private const string DefaultAdminPassword = "2747";
    private const int Pbkdf2Iterations = 100_000;

    /// <summary>로그인 세션 쿠키 이름. 미들웨어(Program.cs)와 컨트롤러가 공유.</summary>
    public const string SessionCookieName = "dsp_session";

    private readonly string _configPath;
    private readonly string _legacyFlagPath;
    private readonly ILogger<DemoAdminService> _logger;
    private readonly ConcurrentDictionary<string, byte> _sessions = new();
    private readonly object _lock = new();
    private DemoAdminConfig _config;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public DemoAdminService(IWebHostEnvironment env, ILogger<DemoAdminService> logger)
    {
        _configPath = Path.Combine(env.ContentRootPath, "demo-admin.json");
        _legacyFlagPath = Path.Combine(env.ContentRootPath, "demo-admin.enabled");
        _logger = logger;
        _config = Load();
    }

    /// <summary>데모 전환(로그인 게이트 + 바로가기 노출) 활성 여부.</summary>
    public bool IsEnabled => _config.Enabled;

    /// <summary>로그인 요구 범위 — "settings"(설정 페이지만) | "app"(첫 화면부터 전체).</summary>
    public string LoginScope => _config.LoginScope;

    // ── 로그인 세션 ──────────────────────────────────────────────────────────

    /// <summary>자격 증명이 맞으면 세션 토큰 발급 후 true. 게이트 활성 여부와 무관하게 검증만 한다(관리 페이지는 항상 로그인 필요).</summary>
    public bool TryLogin(string? id, string? password, out string token)
    {
        token = string.Empty;
        DemoAdminConfig cfg;
        lock (_lock) cfg = _config;

        if (!string.Equals(id?.Trim(), cfg.AdminId, StringComparison.Ordinal) || !VerifyPassword(cfg, password))
            return false;

        token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _sessions[token] = 1;
        return true;
    }

    public bool IsSessionValid(string? token)
        => !string.IsNullOrEmpty(token) && _sessions.ContainsKey(token);

    public void Logout(string? token)
    {
        if (!string.IsNullOrEmpty(token)) _sessions.TryRemove(token, out _);
    }

    // ── 관리 패널 (로그인 세션 필요, 컨트롤러에서 검증) ─────────────────────────

    /// <summary>관리 패널 표시용 설정 스냅샷(비밀번호 해시는 노출하지 않음).</summary>
    public DemoAdminConfigDto GetConfigForAdmin()
    {
        lock (_lock)
        {
            return new DemoAdminConfigDto(
                _config.Enabled,
                _config.LoginScope,
                _config.AdminId,
                _config.Shortcuts
                    .Select(s => new DemoShortcutDto(s.Key, s.Label, s.Href, s.Icon, s.Show))
                    .ToList());
        }
    }

    /// <summary>데모 전환·로그인 범위·바로가기 설정 저장. 자격 증명은 별도(UpdateCredentials).</summary>
    public void UpdateSettings(bool enabled, string? loginScope, IReadOnlyList<DemoShortcutDto>? shortcuts)
    {
        lock (_lock)
        {
            var next = _config.Clone();
            next.Enabled = enabled;
            next.LoginScope = NormalizeScope(loginScope);

            if (shortcuts != null)
            {
                // 알려진 key(기본 바로가기)만 갱신 — 노출 여부·라벨·URL 만 반영, 항목 추가/삭제는 없음.
                foreach (var incoming in shortcuts)
                {
                    var target = next.Shortcuts.FirstOrDefault(s =>
                        string.Equals(s.Key, incoming.Key, StringComparison.OrdinalIgnoreCase));
                    if (target == null) continue;
                    target.Show = incoming.Show;
                    if (!string.IsNullOrWhiteSpace(incoming.Label)) target.Label = incoming.Label.Trim();
                    if (!string.IsNullOrWhiteSpace(incoming.Href)) target.Href = incoming.Href.Trim();
                }
            }

            _config = next;
            Persist(next);
        }
    }

    /// <summary>
    /// 관리자 자격 증명 변경. 현재 비밀번호가 맞아야 적용. newId/newPassword 는 비어 있으면 미변경.
    /// 반환값 false = 현재 비밀번호 불일치(변경 안 함).
    /// </summary>
    public bool UpdateCredentials(string? currentPassword, string? newId, string? newPassword)
    {
        lock (_lock)
        {
            if (!VerifyPassword(_config, currentPassword))
                return false;

            var next = _config.Clone();
            var trimmedId = newId?.Trim();
            if (!string.IsNullOrEmpty(trimmedId))
                next.AdminId = trimmedId;
            if (!string.IsNullOrEmpty(newPassword))
                (next.PasswordHash, next.PasswordSalt) = HashPassword(newPassword);

            _config = next;
            Persist(next);
            return true;
        }
    }

    // ── 사이드바 바로가기 (데모 전환 ON + 개별 show 인 것만) ─────────────────────

    /// <summary>데모 전환이 켜져 있을 때 노출할 외부 바로가기 목록(마스터 스위치). OFF 면 빈 목록.</summary>
    public IReadOnlyList<DemoShortcutDto> GetVisibleShortcuts()
    {
        lock (_lock)
        {
            if (!_config.Enabled) return Array.Empty<DemoShortcutDto>();
            return _config.Shortcuts
                .Where(s => s.Show && !string.IsNullOrWhiteSpace(s.Href))
                .Select(s => new DemoShortcutDto(s.Key, s.Label, s.Href, s.Icon, s.Show))
                .ToList();
        }
    }

    // ── 영속 ──────────────────────────────────────────────────────────────────

    private DemoAdminConfig Load()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                var loaded = JsonSerializer.Deserialize<DemoAdminConfig>(json, JsonOpts);
                if (loaded != null)
                {
                    loaded.Normalize();
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "데모 관리자 설정 로드 실패, 기본값 사용: {Path}", _configPath);
        }

        // 신규 설치 또는 손상 — 기본값. 구 마커 파일(demo-admin.enabled)이 있으면 데모 전환 ON 으로 승계.
        var config = DemoAdminConfig.CreateDefault();
        config.Enabled = File.Exists(_legacyFlagPath);
        return config;
    }

    private void Persist(DemoAdminConfig config)
    {
        try
        {
            File.WriteAllText(_configPath, JsonSerializer.Serialize(config, JsonOpts));
            // 구 마커 파일은 더 이상 사용하지 않음 — 남아 있으면 정리(설정 우선).
            if (File.Exists(_legacyFlagPath))
                File.Delete(_legacyFlagPath);
        }
        catch (Exception ex)
        {
            // 파일 실패해도 인메모리 상태는 갱신(현 프로세스 동작 우선). 재시작 시 파일 기준으로 복원된다.
            _logger.LogError(ex, "데모 관리자 설정 저장 실패: {Path}", _configPath);
        }
    }

    // ── 비밀번호 해시 ──────────────────────────────────────────────────────────

    private static bool VerifyPassword(DemoAdminConfig cfg, string? password)
    {
        // 비밀번호를 아직 바꾸지 않은 초기 상태 — 기본 비밀번호와 상수시간 비교.
        if (string.IsNullOrEmpty(cfg.PasswordHash) || string.IsNullOrEmpty(cfg.PasswordSalt))
            return CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(password ?? string.Empty),
                System.Text.Encoding.UTF8.GetBytes(DefaultAdminPassword));

        try
        {
            var salt = Convert.FromHexString(cfg.PasswordSalt);
            var expected = Convert.FromHexString(cfg.PasswordHash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                password ?? string.Empty, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch
        {
            return false;
        }
    }

    private static (string Hash, string Salt) HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        return (Convert.ToHexString(hash), Convert.ToHexString(salt));
    }

    private static string NormalizeScope(string? scope)
        => string.Equals(scope?.Trim(), "app", StringComparison.OrdinalIgnoreCase) ? "app" : "settings";
}

// ── 영속 모델 (demo-admin.json) ──────────────────────────────────────────────

/// <summary>demo-admin.json 직렬화 모델. 관리 API 로만 갱신되며 AppSettings 와 분리 유지.</summary>
public class DemoAdminConfig
{
    public bool Enabled { get; set; }
    public string LoginScope { get; set; } = "settings";
    public string AdminId { get; set; } = "admin";
    /// <summary>PBKDF2 해시(hex). null/빈 값이면 기본 비밀번호(2747) 사용 상태.</summary>
    public string? PasswordHash { get; set; }
    public string? PasswordSalt { get; set; }
    public List<DemoShortcutConfig> Shortcuts { get; set; } = new();

    public static DemoAdminConfig CreateDefault() => new()
    {
        Enabled = false,
        LoginScope = "settings",
        AdminId = "admin",
        PasswordHash = null,
        PasswordSalt = null,
        Shortcuts = DefaultShortcuts(),
    };

    private static List<DemoShortcutConfig> DefaultShortcuts() => new()
    {
        new DemoShortcutConfig { Key = "equipmentDoctor", Label = "설비박사 챗봇",       Href = "http://121.139.3.28:2748/", Icon = "smart_toy", Show = true },
        new DemoShortcutConfig { Key = "reverseAi",       Label = "ReverseAI PLCtoAASX", Href = "http://121.139.3.28:2747",  Icon = "sync_alt",  Show = true },
    };

    /// <summary>로드 후 누락/구버전 필드 보정 — 기본 바로가기가 빠져 있으면 채워 넣는다.</summary>
    public void Normalize()
    {
        LoginScope = string.Equals(LoginScope, "app", StringComparison.OrdinalIgnoreCase) ? "app" : "settings";
        if (string.IsNullOrWhiteSpace(AdminId)) AdminId = "admin";
        Shortcuts ??= new List<DemoShortcutConfig>();
        foreach (var def in DefaultShortcuts())
        {
            var existing = Shortcuts.FirstOrDefault(s =>
                string.Equals(s.Key, def.Key, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                Shortcuts.Add(def);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(existing.Label)) existing.Label = def.Label;
                if (string.IsNullOrWhiteSpace(existing.Href)) existing.Href = def.Href;
                if (string.IsNullOrWhiteSpace(existing.Icon)) existing.Icon = def.Icon;
            }
        }
    }

    public DemoAdminConfig Clone() => new()
    {
        Enabled = Enabled,
        LoginScope = LoginScope,
        AdminId = AdminId,
        PasswordHash = PasswordHash,
        PasswordSalt = PasswordSalt,
        Shortcuts = Shortcuts.Select(s => new DemoShortcutConfig
        {
            Key = s.Key, Label = s.Label, Href = s.Href, Icon = s.Icon, Show = s.Show,
        }).ToList(),
    };
}

public class DemoShortcutConfig
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Href { get; set; } = string.Empty;
    public string Icon { get; set; } = "open_in_new";
    public bool Show { get; set; } = true;
}

// ── DTO (camelCase) ──────────────────────────────────────────────────────────

public record DemoAdminConfigDto(
    bool Enabled,
    string LoginScope,
    string AdminId,
    List<DemoShortcutDto> Shortcuts);

public record DemoShortcutDto(string Key, string Label, string Href, string Icon, bool Show);
