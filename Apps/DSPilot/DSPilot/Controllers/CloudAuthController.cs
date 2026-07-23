// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Services.CloudAuth;
using Microsoft.AspNetCore.Mvc;

namespace DSPilot.Controllers;

/// <summary>
/// CloudWorks(프로비저닝 서버) 계정 연동 — 회원가입/로그인 프록시.
/// 브라우저(설정 › 클라우드 계정 페이지)는 이 컨트롤러만 부르고, 컨트롤러가 프로비저닝 서버로 프록시한다.
/// 서버 URL 은 <see cref="CloudAuthOptions.BaseUrl"/>(Secrets 주입)에만 있어 브라우저로 새지 않는다.
/// 세션 토큰은 서버측 <see cref="CloudSessionStore"/>(앱 전역, 한 사이트=한 계정)에 보관하고 브라우저엔 노출하지 않는다.
/// </summary>
[ApiController]
[Route("api/cloud-auth")]
public class CloudAuthController : ControllerBase
{
    private readonly IProvisioningAuthClient _client;
    private readonly CloudSessionStore _session;
    private readonly CloudAuthOptions _opts;

    public CloudAuthController(IProvisioningAuthClient client, CloudSessionStore session, CloudAuthOptions opts)
    {
        _client = client;
        _session = session;
        _opts = opts;
    }

    public record RegisterRequest(string? LoginId, string? Password);
    public record LoginRequest(string? LoginId, string? Password);

    /// <summary>현재 로그인 상태 + 연동 구성 여부(토큰은 미노출).</summary>
    [HttpGet("status")]
    public IActionResult Status()
    {
        var (loggedIn, adminId, loginId, displayName) = _session.Snapshot();
        return Ok(new { configured = _opts.Configured, loggedIn, adminId, loginId, displayName });
    }

    /// <summary>회원가입. 서버가 가입 직후 세션을 발급하므로 성공 시 바로 로그인 상태가 된다.</summary>
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req?.LoginId) || string.IsNullOrWhiteSpace(req?.Password))
            return BadRequest(new { message = "아이디와 비밀번호를 입력하세요." });

        var issues = PasswordPolicy.GetIssues(req.Password!);
        if (issues.Count > 0)
            return BadRequest(new { message = $"비밀번호 조건 미충족: {string.Join(", ", issues)}" });

        var r = await _client.RegisterAsync(req.LoginId!.Trim(), req.Password!, ct);
        if (!r.Ok)
            return BadRequest(new { message = r.Message });

        _session.Set(r.SessionToken!, r.AdminId, req.LoginId!.Trim(), r.DisplayName);
        return Ok(new { ok = true, displayName = r.DisplayName ?? req.LoginId!.Trim() });
    }

    /// <summary>로그인. 성공 시 세션을 서버측에 보관.</summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req?.LoginId) || string.IsNullOrWhiteSpace(req?.Password))
            return BadRequest(new { message = "아이디와 비밀번호를 입력하세요." });

        var r = await _client.LoginAsync(req.LoginId!.Trim(), req.Password!, ct);
        if (!r.Ok)
            return Unauthorized(new { message = r.Message });

        _session.Set(r.SessionToken!, r.AdminId, req.LoginId!.Trim(), r.DisplayName);
        return Ok(new { ok = true, displayName = r.DisplayName ?? req.LoginId!.Trim() });
    }

    /// <summary>로그아웃 — 서버측 세션 소거(프로비저닝 서버 토큰은 TTL 로 자연 만료).</summary>
    [HttpPost("logout")]
    public IActionResult Logout()
    {
        _session.Clear();
        return Ok(new { ok = true });
    }

    /// <summary>연동 검증 — 로그인 세션으로 계정 트리(사이트&gt;단말) 조회.</summary>
    [HttpGet("overview")]
    public async Task<IActionResult> Overview(CancellationToken ct)
    {
        var token = _session.Token;
        if (string.IsNullOrEmpty(token))
            return Unauthorized(new { message = "로그인이 필요합니다." });

        var r = await _client.OverviewAsync(token, ct);
        if (!r.Ok)
            return BadRequest(new { message = r.Message });

        return Ok(new
        {
            ok = true,
            accountName = r.AccountName,
            sites = r.Sites.Select(s => new
            {
                siteId = s.SiteId,
                displayName = s.DisplayName,
                edges = s.Edges.Select(e => new
                {
                    edgeId = e.EdgeId,
                    status = e.Status,
                    instanceStatus = e.InstanceStatus,
                    publicIp = e.PublicIp
                })
            })
        });
    }
}
