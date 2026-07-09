// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace DSPilot.Services;

/// <summary>
/// 데모용 임시 관리자 게이트 (설정 페이지 보호).
/// /demo/admin 활성화 페이지에서 코드(2747) 입력 시 게이트 on/off 토글 — 이후 /settings 진입에
/// admin 로그인(쿠키 세션)을 요구한다. 활성/비활성 상태는 어떤 API 응답에도 노출하지 않는다
/// (toggle 응답도 상태 무관 동일 메시지). AppSettings 와 분리된 마커 파일로 영속하므로
/// GET /api/settings 스냅샷에도 나타나지 않는다.
/// 세션 토큰은 인메모리 전용 — 서버 재시작/게이트 토글 시 전부 무효화(재로그인 필요). 데모 용도로 충분.
/// </summary>
public class DemoAdminService
{
    private const string ActivationCode = "2747";
    private const string AdminId = "admin";
    private const string AdminPassword = "2747";

    /// <summary>로그인 세션 쿠키 이름. 미들웨어(Program.cs)와 컨트롤러가 공유.</summary>
    public const string SessionCookieName = "dsp_session";

    private readonly string _flagPath;
    private readonly ILogger<DemoAdminService> _logger;
    private readonly ConcurrentDictionary<string, byte> _sessions = new();
    private readonly object _toggleLock = new();
    private volatile bool _enabled;

    public DemoAdminService(IWebHostEnvironment env, ILogger<DemoAdminService> logger)
    {
        _flagPath = Path.Combine(env.ContentRootPath, "demo-admin.enabled");
        _logger = logger;
        _enabled = File.Exists(_flagPath);
    }

    /// <summary>설정 페이지 로그인 게이트 활성 여부.</summary>
    public bool IsEnabled => _enabled;

    /// <summary>
    /// 활성화 코드가 맞으면 게이트 상태를 토글하고 true. 토글 시 기존 로그인 세션 전부 무효화
    /// (off→on 재활성 시 이전 쿠키가 그대로 통과하는 것 방지).
    /// </summary>
    public bool TryToggle(string? code)
    {
        if (!string.Equals(code?.Trim(), ActivationCode, StringComparison.Ordinal))
            return false;

        lock (_toggleLock)
        {
            var next = !_enabled;
            try
            {
                if (next)
                    File.WriteAllText(_flagPath, string.Empty);
                else if (File.Exists(_flagPath))
                    File.Delete(_flagPath);
            }
            catch (Exception ex)
            {
                // 파일 실패해도 인메모리 상태는 토글(현 프로세스 동작 우선). 재시작 시 파일 기준으로 복원된다.
                _logger.LogError(ex, "데모 관리자 게이트 마커 파일 갱신 실패: {Path}", _flagPath);
            }
            _enabled = next;
            _sessions.Clear();
        }
        return true;
    }

    /// <summary>자격 증명이 맞으면 세션 토큰 발급 후 true. 게이트 활성 여부와 무관하게 검증만 한다(상태 비노출).</summary>
    public bool TryLogin(string? id, string? password, out string token)
    {
        token = string.Empty;
        if (!string.Equals(id?.Trim(), AdminId, StringComparison.Ordinal)
            || !string.Equals(password, AdminPassword, StringComparison.Ordinal))
            return false;

        token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _sessions[token] = 1;
        return true;
    }

    public bool IsSessionValid(string? token)
        => !string.IsNullOrEmpty(token) && _sessions.ContainsKey(token);
}
