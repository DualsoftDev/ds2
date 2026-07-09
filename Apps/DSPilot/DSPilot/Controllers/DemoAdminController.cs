// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Services;
using Microsoft.AspNetCore.Mvc;

namespace DSPilot.Controllers;

/// <summary>
/// 데모용 관리자 게이트 API (설정 페이지 보호 — DemoAdminService 참조).
/// POST 는 antiforgery 미적용 평범한 JSON fetch (SettingsController 와 동일).
/// 두 응답 모두 게이트 활성/비활성 상태를 노출하지 않는다 — toggle 은 결과 상태와 무관하게
/// 동일 메시지, login 은 게이트 꺼져 있어도 자격만 검증한다.
/// </summary>
[ApiController]
[Route("api/demo-admin")]
public class DemoAdminController : ControllerBase
{
    private readonly DemoAdminService _demoAdmin;

    public DemoAdminController(DemoAdminService demoAdmin) => _demoAdmin = demoAdmin;

    public record ToggleRequest(string? Code);
    public record LoginRequest(string? Id, string? Password);

    /// <summary>활성화 페이지(/demo/admin)의 코드 입력. 코드 일치 시 게이트 토글.</summary>
    [HttpPost("toggle")]
    public IActionResult Toggle([FromBody] ToggleRequest request)
    {
        if (!_demoAdmin.TryToggle(request?.Code))
            return BadRequest(new { message = "코드가 올바르지 않습니다." });
        return Ok(new { message = "적용되었습니다." });
    }

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
            // Expires 미지정 = 브라우저 세션 쿠키. 서버측 토큰도 재시작/토글 시 소멸.
        });
        return Ok(new { ok = true });
    }
}
