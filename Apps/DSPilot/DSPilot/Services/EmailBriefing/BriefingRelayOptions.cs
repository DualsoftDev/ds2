// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
namespace DSPilot.Services.EmailBriefing;

/// <summary>
/// 회사 메일서버(yourcompany.com Microsoft 365) 고정 릴레이 설정. 비밀이 아닌 기본값(host/port/tls/표시명)은
/// 코드에 박혀 있고, <b>계정·비밀번호 등 자격증명은 설치 시 주입</b>한다(git·설치본 소스에 평문 미포함).
///
/// 주입 경로(둘 중 편한 것 — <c>IConfiguration</c> 의 "BriefingRelay" 섹션으로 병합됨):
///   ① 설치 스크립트가 배포 폴더에 <c>appsettings.Secrets.json</c> 를 배치:
///        { "BriefingRelay": { "User": "briefing@yourcompany.com", "Password": "…", "FromAddress": "briefing@yourcompany.com" } }
///   ② 환경변수: <c>BriefingRelay__User</c> / <c>BriefingRelay__Password</c> / <c>BriefingRelay__FromAddress</c>
///
/// <see cref="Locked"/>=true(기본) 이면 설정 UI 에서 SMTP·발신·모드를 숨기고 사용자는 활성/수신자/스케줄만 만진다.
/// 다른 배포(사내·타사)에서 사용자가 직접 SMTP 를 넣게 하려면 이 값을 false 로 주입하면 기존 전체 UI 가 노출된다.
/// </summary>
public sealed class BriefingRelayOptions
{
    /// <summary>
    /// 발송 경로. "api"(기본·권장) = 회사 중앙 발송 API(BriefingRelay)를 호출 — 메일 자격증명이 고객 PC 에 없음.
    /// "smtp" = 이 PC 가 직접 O365 SMTP 로 발송(계정/비번이 이 PC 에 주입돼야 함 — 노출 위험, 사내/소규모용).
    /// </summary>
    public string Mode { get; set; } = "api";

    // ── api 모드(중앙 발송 API) ──
    /// <summary>BriefingRelay 서버 베이스 URL. 예: https://relay.yourcompany.com (경로 /api/briefing/send 는 클라이언트가 붙임).</summary>
    public string ApiUrl { get; set; } = "";

    /// <summary>이 설치에 발급된 API 키(저위험 토큰 — 유출 시 이 키만 폐기). 설치 시 주입.</summary>
    public string ApiKey { get; set; } = "";

    // ── smtp 모드(직접 발송) ──
    /// <summary>SMTP 호스트. 기본 Microsoft 365.</summary>
    public string Host { get; set; } = "smtp.office365.com";

    /// <summary>SMTP 포트. 기본 587(STARTTLS).</summary>
    public int Port { get; set; } = 587;

    /// <summary>TLS 사용. 기본 true(587=STARTTLS).</summary>
    public bool UseTls { get; set; } = true;

    /// <summary>인증 계정(사서함). 설치 시 주입(예: briefing@yourcompany.com). 미주입이면 발송이 인증 오류로 실패한다.</summary>
    public string User { get; set; } = "";

    /// <summary>인증 비밀번호(또는 앱 비밀번호). 설치 시 주입 — 소스/커밋에 두지 말 것.</summary>
    public string Password { get; set; } = "";

    /// <summary>발신 주소(From). 비우면 <see cref="User"/> 사용. O365 는 보통 계정과 일치해야 한다.</summary>
    public string FromAddress { get; set; } = "";

    /// <summary>발신자 표시명.</summary>
    public string FromName { get; set; } = "DSPilot 브리핑";

    /// <summary>true(기본): 고정 릴레이 사용 + 설정 UI 에서 SMTP/발신/모드 숨김. false: 사용자 직접 SMTP 설정 노출.</summary>
    public bool Locked { get; set; } = true;

    /// <summary>api 모드 여부.</summary>
    public bool IsApiMode => string.Equals(Mode, "api", StringComparison.OrdinalIgnoreCase);

    /// <summary>발송 준비 완료 여부(모드별): api=ApiUrl+ApiKey, smtp=User+Password.</summary>
    public bool CredentialConfigured => IsApiMode
        ? !string.IsNullOrWhiteSpace(ApiUrl) && !string.IsNullOrWhiteSpace(ApiKey)
        : !string.IsNullOrWhiteSpace(User) && !string.IsNullOrWhiteSpace(Password);

    /// <summary>표시/발송에 쓸 실효 From(비우면 User). api 모드에선 실제 From 은 서버가 정함(표시용).</summary>
    public string EffectiveFrom => string.IsNullOrWhiteSpace(FromAddress) ? User : FromAddress;
}
