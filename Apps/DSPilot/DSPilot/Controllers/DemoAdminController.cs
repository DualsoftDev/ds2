// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Services;
using Microsoft.AspNetCore.Mvc;

namespace DSPilot.Controllers;

/// <summary>
/// 데모용 관리자 게이트 + 데모 설정 API (DemoAdminService 참조).
/// POST 는 antiforgery 미적용 평범한 JSON fetch (SettingsController 와 동일).
/// login 은 게이트 활성 여부와 무관하게 자격만 검증한다. config/credentials 관리 API 는 로그인 세션이
/// 있어야만 동작한다 — 로그인 전에는 설정 상태를 노출하지 않는다.
/// </summary>
[ApiController]
[Route("api/demo-admin")]
public class DemoAdminController : ControllerBase
{
    private readonly DemoAdminService _demoAdmin;

    public DemoAdminController(DemoAdminService demoAdmin) => _demoAdmin = demoAdmin;

    public record LoginRequest(string? Id, string? Password);
    public record ShortcutInput(string? Key, string? Label, string? Href, bool Show);
    public record UpdateSettingsRequest(bool Enabled, string? LoginScope, List<ShortcutInput>? Shortcuts);
    public record UpdateCredentialsRequest(string? CurrentPassword, string? NewId, string? NewPassword);

    /// <summary>관리자 로그인. 성공 시 HttpOnly 세션 쿠키 발급(브라우저 세션 한정, 재시작 시 무효).</summary>
    [HttpPost("login")]
    public IActionResult Login([FromBody] LoginRequest request)
    {
        if (!_demoAdmin.TryLogin(request?.Id, request?.Password, out var token))
            return Unauthorized(new { message = "아이디 또는 비밀번호가 올바르지 않습니다." });

        Response.Cookies.Append(DemoAdminService.SessionCookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = false,   // LAN HTTP 배포 (UseHsts 비활성) — Secure 강제 시 쿠키 유실
            Path = "/",
            IsEssential = true,
            // Expires 미지정 = 브라우저 세션 쿠키. 서버측 토큰도 재시작 시 소멸.
        });
        return Ok(new { ok = true });
    }

    /// <summary>로그아웃 — 세션 무효화 + 쿠키 삭제.</summary>
    [HttpPost("logout")]
    public IActionResult Logout()
    {
        _demoAdmin.Logout(Request.Cookies[DemoAdminService.SessionCookieName]);
        Response.Cookies.Delete(DemoAdminService.SessionCookieName, new CookieOptions { Path = "/" });
        return Ok(new { ok = true });
    }

    /// <summary>현재 데모 설정 스냅샷(관리 패널용). 로그인 세션 필요 — 없으면 401.</summary>
    [HttpGet("config")]
    public IActionResult GetConfig()
    {
        if (!IsAuthenticated())
            return Unauthorized(new { message = "로그인이 필요합니다." });
        return Ok(_demoAdmin.GetConfigForAdmin());
    }

    /// <summary>데모 전환·로그인 범위·바로가기 설정 저장. 로그인 세션 필요.</summary>
    [HttpPost("config")]
    public IActionResult UpdateConfig([FromBody] UpdateSettingsRequest request)
    {
        if (!IsAuthenticated())
            return Unauthorized(new { message = "로그인이 필요합니다." });
        if (request == null)
            return BadRequest(new { message = "요청 본문이 없습니다." });

        var shortcuts = request.Shortcuts?
            .Select(s => new DemoShortcutDto(s.Key ?? string.Empty, s.Label ?? string.Empty, s.Href ?? string.Empty, string.Empty, s.Show))
            .ToList();

        _demoAdmin.UpdateSettings(request.Enabled, request.LoginScope, shortcuts);
        return Ok(new { ok = true, config = _demoAdmin.GetConfigForAdmin() });
    }

    /// <summary>관리자 아이디·비밀번호 변경. 현재 비밀번호가 맞아야 적용. 로그인 세션 필요.</summary>
    [HttpPost("credentials")]
    public IActionResult UpdateCredentials([FromBody] UpdateCredentialsRequest request)
    {
        if (!IsAuthenticated())
            return Unauthorized(new { message = "로그인이 필요합니다." });
        if (request == null)
            return BadRequest(new { message = "요청 본문이 없습니다." });

        if (!_demoAdmin.UpdateCredentials(request.CurrentPassword, request.NewId, request.NewPassword))
            return BadRequest(new { message = "현재 비밀번호가 올바르지 않습니다." });

        return Ok(new { ok = true });
    }

    private bool IsAuthenticated()
        => _demoAdmin.IsSessionValid(Request.Cookies[DemoAdminService.SessionCookieName]);
}
