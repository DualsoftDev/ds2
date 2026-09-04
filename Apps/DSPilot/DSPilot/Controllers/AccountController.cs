// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Services;
using Microsoft.AspNetCore.Mvc;

namespace DSPilot.Controllers;

/// <summary>
/// 관리자 계정 API — 설정 ▸ 고급 ▸ "계정·로그인" 카드(settings.html)와 로그인 관문(admin-login.html)이 소비.
/// 2026-09-04 데모 관리 패널(/api/demo-admin/credentials, 로그인 범위)에서 정규 기능으로 이관.
/// POST 는 antiforgery 미적용 평범한 JSON fetch (SettingsController 와 동일).
///
/// 접근 규칙(CanManage): 로그인 세션이 있거나, 계정이 비활성(로그인 게이트 OFF)이면 관리 가능.
///   - 게이트 OFF 상태에선 설정 페이지 자체가 누구에게나 열려 있으므로 계정 카드도 같은 노출 수준이다.
///   - 게이트 ON 이면 설정 페이지 진입에 이미 로그인이 필요하므로 fetch 에 세션 쿠키가 실려 온다.
/// 잠금 방지: 세션 없이 계정을 *활성화*할 때는 현재 비밀번호를 반드시 확인한다(비밀번호를 모른 채 켜서
///   설정 페이지에서 쫓겨나는 사고 방지). 확인이 맞으면 세션 쿠키도 함께 발급해 즉시 재로그인을 요구하지 않는다.
/// </summary>
[ApiController]
[Route("api/account")]
public class AccountController : ControllerBase
{
    private readonly DemoAdminService _account;

    public AccountController(DemoAdminService account) => _account = account;

    public record LoginRequest(string? Id, string? Password);
    public record LoginGateRequest(bool Enabled, string? LoginScope, string? CurrentPassword);
    public record UpdateCredentialsRequest(string? CurrentPassword, string? NewId, string? NewPassword);

    /// <summary>관리자 로그인. 성공 시 HttpOnly 세션 쿠키 발급(브라우저 세션 한정, 재시작 시 무효).</summary>
    [HttpPost("login")]
    public IActionResult Login([FromBody] LoginRequest request)
    {
        if (!_account.TryLogin(request?.Id, request?.Password, out var token))
            return Unauthorized(new { message = "아이디 또는 비밀번호가 올바르지 않습니다." });

        Response.Cookies.Append(DemoAdminService.SessionCookieName, token, DemoAdminService.SessionCookieOptions());
        return Ok(new { ok = true });
    }

    /// <summary>로그아웃 — 세션 무효화 + 쿠키 삭제.</summary>
    [HttpPost("logout")]
    public IActionResult Logout()
    {
        _account.Logout(Request.Cookies[DemoAdminService.SessionCookieName]);
        Response.Cookies.Delete(DemoAdminService.SessionCookieName, new CookieOptions { Path = "/" });
        return Ok(new { ok = true });
    }

    /// <summary>계정 카드 스냅샷(활성 여부·범위·아이디·초기 비밀번호 여부·세션 유무).</summary>
    [HttpGet]
    public IActionResult Get()
    {
        if (!CanManage())
            return Unauthorized(new { message = "로그인이 필요합니다." });
        return Ok(_account.GetAccountInfo(IsAuthenticated()));
    }

    /// <summary>계정 활성화(로그인 게이트) on/off + 적용 범위. 세션 없이 켤 때는 현재 비밀번호 확인 필수.</summary>
    [HttpPost("login-gate")]
    public IActionResult UpdateLoginGate([FromBody] LoginGateRequest request)
    {
        if (!CanManage())
            return Unauthorized(new { message = "로그인이 필요합니다." });
        if (request == null)
            return BadRequest(new { message = "요청 본문이 없습니다." });

        var authenticated = IsAuthenticated();
        if (request.Enabled && !authenticated)
        {
            if (!_account.VerifyCurrentPassword(request.CurrentPassword))
                return BadRequest(new { message = "현재 비밀번호가 올바르지 않습니다. 잠금 방지를 위해 비밀번호 확인 후 활성화됩니다." });

            // 비밀번호를 확인했으니 세션도 발급 — 활성화 직후 같은 브라우저가 로그인 화면으로 튕기지 않게.
            Response.Cookies.Append(DemoAdminService.SessionCookieName, _account.IssueSession(), DemoAdminService.SessionCookieOptions());
            authenticated = true;
        }

        _account.UpdateLoginGate(request.Enabled, request.LoginScope);
        return Ok(new { ok = true, account = _account.GetAccountInfo(authenticated) });
    }

    /// <summary>관리자 아이디·비밀번호 변경. 현재 비밀번호가 맞아야 적용.</summary>
    [HttpPost("credentials")]
    public IActionResult UpdateCredentials([FromBody] UpdateCredentialsRequest request)
    {
        if (!CanManage())
            return Unauthorized(new { message = "로그인이 필요합니다." });
        if (request == null)
            return BadRequest(new { message = "요청 본문이 없습니다." });

        if (!_account.UpdateCredentials(request.CurrentPassword, request.NewId, request.NewPassword))
            return BadRequest(new { message = "현재 비밀번호가 올바르지 않습니다." });

        return Ok(new { ok = true, account = _account.GetAccountInfo(IsAuthenticated()) });
    }

    private bool IsAuthenticated()
        => _account.IsSessionValid(Request.Cookies[DemoAdminService.SessionCookieName]);

    private bool CanManage() => IsAuthenticated() || !_account.IsLoginEnabled;
}
