// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Collections.Generic;

namespace DSPilot.Services.CloudAuth;

/// <summary>
/// CloudWorks 프로비저닝 서버의 관리자 인증 API 호출 계약(서버측 프록시).
/// 브라우저는 이 클라이언트를 직접 부르지 않고 DSPilot 의 <c>/api/cloud-auth/*</c> 컨트롤러를 거친다 —
/// 서버 URL 이 브라우저로 새지 않도록. 실제 URL 은 <see cref="CloudAuthOptions.BaseUrl"/>(Secrets 주입)에만 있다.
///
/// ── 서버 계약(Provisioning-server app/routers/admin.py) ──
///   POST /api/admin/register {login_id, password, display_name?, company_name?} → {admin_id, admin_session}
///   POST /api/admin/login    {login_id, password}                              → {admin_id, admin_session, display_name}
///   GET  /api/account/overview  (헤더 X-Admin-Session)                          → {account, network, sites[...]}
/// </summary>
public interface IProvisioningAuthClient
{
    /// <summary>회원가입. 성공 시 즉시 세션 토큰(서버가 가입 직후 발급)까지 담아 반환.</summary>
    Task<CloudAuthResult> RegisterAsync(string loginId, string password, string? displayName, string? companyName, CancellationToken ct);

    /// <summary>로그인. 성공 시 세션 토큰 + 표시명 반환.</summary>
    Task<CloudAuthResult> LoginAsync(string loginId, string password, CancellationToken ct);

    /// <summary>로그인 세션으로 계정 트리(사이트&gt;단말) 조회. 연동 검증·계정 화면용.</summary>
    Task<CloudOverviewResult> OverviewAsync(string sessionToken, CancellationToken ct);
}

/// <summary>register/login 공통 결과 — UI 가 쓰는 최소 표면만.</summary>
public sealed record CloudAuthResult(bool Ok, string? AdminId, string? SessionToken, string? DisplayName, string? Message)
{
    public static CloudAuthResult Success(string? adminId, string? token, string? displayName)
        => new(true, adminId, token, displayName, null);
    public static CloudAuthResult Fail(string message) => new(false, null, null, null, message);
}

/// <summary>계정 트리 조회 결과(사이트 &gt; 단말). 서버 스키마 전체가 아니라 표시용 최소 필드만 파싱.</summary>
public sealed record CloudOverviewResult(bool Ok, string? AccountName, IReadOnlyList<CloudSite> Sites, string? Message)
{
    public static CloudOverviewResult Fail(string message)
        => new(false, null, System.Array.Empty<CloudSite>(), message);
}

/// <summary>사이트 노드(자식 = 단말).</summary>
public sealed record CloudSite(string SiteId, string DisplayName, IReadOnlyList<CloudEdge> Edges);

/// <summary>단말 노드. 인스턴스 상태/공인 IP 는 표시용.</summary>
public sealed record CloudEdge(string EdgeId, string Status, string? InstanceStatus, string? PublicIp);
