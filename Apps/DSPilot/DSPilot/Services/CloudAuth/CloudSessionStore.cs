// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
namespace DSPilot.Services.CloudAuth;

/// <summary>
/// 이 DSPilot 설치(=한 현장)의 CloudWorks 클라우드 계정 세션 — 앱 전역 싱글톤.
/// 한 사이트 = 한 클라우드 관리자 계정이므로 브라우저별이 아니라 프로세스 1개 세션을 공유한다
/// (Promaker 의 <c>PvSession</c> 과 동형). 토큰만 메모리에 보관하고 비밀번호는 저장하지 않는다.
///
/// 영속성: 프로비저닝 서버의 세션은 서버 인메모리(TTL) 라 서버·DSPilot 재시작 시 무효화된다 —
/// 여기서도 인메모리로만 들고, 재시작 후 재로그인을 요구한다. 표시용 로그인 아이디/이름만 가볍다.
/// </summary>
public sealed class CloudSessionStore
{
    private readonly object _lock = new();
    private string? _token;
    private string? _adminId;
    private string? _loginId;
    private string? _displayName;

    /// <summary>세션 토큰(로그인/가입 성공 시 설정). null=로그아웃.</summary>
    public string? Token { get { lock (_lock) return _token; } }

    /// <summary>로그인 상태.</summary>
    public bool IsLoggedIn { get { lock (_lock) return !string.IsNullOrEmpty(_token); } }

    /// <summary>표시용 스냅샷(현재 로그인 정보).</summary>
    public (bool loggedIn, string? adminId, string? loginId, string? displayName) Snapshot()
    {
        lock (_lock) return (!string.IsNullOrEmpty(_token), _adminId, _loginId, _displayName);
    }

    /// <summary>로그인/가입 성공 결과를 세션에 설정.</summary>
    public void Set(string token, string? adminId, string loginId, string? displayName)
    {
        lock (_lock)
        {
            _token = token;
            _adminId = adminId;
            _loginId = loginId;
            _displayName = string.IsNullOrWhiteSpace(displayName) ? loginId : displayName;
        }
    }

    /// <summary>로그아웃 — 전부 소거.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _token = null;
            _adminId = null;
            _loginId = null;
            _displayName = null;
        }
    }
}
