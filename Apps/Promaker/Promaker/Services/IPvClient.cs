using System.Collections.Generic;

namespace Promaker.Services;

/// <summary>
/// PV(프로비저닝) 서버 통신 계약의 C# 표면.
///
/// 실제 구현·서버 URL·엔드포인트·프로토콜은 네이티브 <c>ExternalDlls/PvClient.dll</c>(C++, git 제외)
/// 안에 있다. 이 인터페이스와 <see cref="PvClient"/> 래퍼는 "무엇을 호출한다"만 알고
/// "어디로/어떻게"는 모른다. 서버 IP·계정은 pv.conf(로컬)에만 있어 public 저장소에 노출되지 않는다.
/// (조회 결과의 필드명 파싱은 C# 에서 하되, 이는 IP·자격증명이 아닌 표시용 라벨 수준이다.)
/// </summary>
public interface IPvClient
{
    /// <summary>로그인. 성공 시 세션 토큰(<see cref="PvLoginResult.Token"/>)을 담아 반환.</summary>
    PvLoginResult Login(string loginId, string password);

    /// <summary>회원가입. 성공/실패와 사유만 반환.</summary>
    PvResult Register(PvRegisterRequest request);

    /// <summary>아이디/비밀번호 찾기 요청. 성공/실패와 안내 메시지 반환.</summary>
    PvResult FindCredentials(string loginIdOrEmail);

    /// <summary>로그인 후 내 계정의 사이트&gt;단말 트리 조회 (업로드 타겟 선택용).</summary>
    PvOverviewResult Overview(string token);
}

/// <summary>로그인 결과. 서버 스키마가 아니라 UI 가 쓰는 최소 표면만 담는다.</summary>
public sealed record PvLoginResult(bool Ok, string? Token, string? Message)
{
    public static PvLoginResult Success(string token) => new(true, token, null);
    public static PvLoginResult Fail(string message) => new(false, null, message);
}

/// <summary>가입/찾기 등 토큰이 필요 없는 요청의 공통 결과.</summary>
public sealed record PvResult(bool Ok, string? Message)
{
    public static PvResult Success(string? message = null) => new(true, message);
    public static PvResult Fail(string message) => new(false, message);
}

/// <summary>회원가입 입력. admin_user 컬럼과 1:1 이 아니라, UI 가 받는 최소 필드만.</summary>
public sealed record PvRegisterRequest(string LoginId, string Password, string DisplayName, string CompanyName);

/// <summary>사이트&gt;단말 트리 조회 결과.</summary>
public sealed record PvOverviewResult(bool Ok, IReadOnlyList<PvSite> Sites, string? Message)
{
    public static PvOverviewResult Fail(string message) => new(false, System.Array.Empty<PvSite>(), message);
}

/// <summary>업로드 타겟 트리 — 사이트 노드(자식 = 단말).</summary>
public sealed record PvSite(string SiteId, string DisplayName, IReadOnlyList<PvEdge> Edges);

/// <summary>업로드 타겟 트리 — 단말 노드(전송 대상). 인스턴스 상태/IP 는 표시용.</summary>
public sealed record PvEdge(string EdgeId, string DisplayName, string Status, string? InstanceStatus, string? PublicIp);
