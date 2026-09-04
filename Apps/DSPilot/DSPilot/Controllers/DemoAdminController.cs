// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Services;
using Microsoft.AspNetCore.Mvc;

namespace DSPilot.Controllers;

/// <summary>
/// 데모 관리 패널 API(/demo/admin) — 데모 전환 + 바로가기만 남았다.
/// 계정 활성화(로그인 게이트)·아이디/비밀번호 변경은 2026-09-04 정규 설정(/api/account/*, 설정 ▸ 고급)으로 이관.
/// POST 는 antiforgery 미적용 평범한 JSON fetch (SettingsController 와 동일).
/// login 은 게이트 활성 여부와 무관하게 자격만 검증한다. config 관리 API 는 로그인 세션이 있어야만 동작한다.
/// </summary>
[ApiController]
[Route("api/demo-admin")]
public class DemoAdminController : ControllerBase
{
    private readonly DemoAdminService _demoAdmin;

    public DemoAdminController(DemoAdminService demoAdmin) => _demoAdmin = demoAdmin;

    public record LoginRequest(string? Id, string? Password);
    public record ShortcutInput(string? Key, string? Label, string? Href, bool Show);
    public record UpdateSettingsRequest(bool Enabled, List<ShortcutInput>? Shortcuts);

    /// <summary>관리자 로그인(데모 패널용 — 계정 정본은 /api/account/login 과 동일 자격).</summary>
    [HttpPost("login")]
    public IActionResult Login([FromBody] LoginRequest request)
    {
        if (!_demoAdmin.TryLogin(request?.Id, request?.Password, out var token))
            return Unauthorized(new { message = "아이디 또는 비밀번호가 올바르지 않습니다." });

        Response.Cookies.Append(DemoAdminService.SessionCookieName, token, DemoAdminService.SessionCookieOptions());
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

    /// <summary>데모 전환·바로가기 설정 저장. 로그인 세션 필요.</summary>
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

        _demoAdmin.UpdateSettings(request.Enabled, shortcuts);
        return Ok(new { ok = true, config = _demoAdmin.GetConfigForAdmin() });
    }

    private bool IsAuthenticated()
        => _demoAdmin.IsSessionValid(Request.Cookies[DemoAdminService.SessionCookieName]);
}
