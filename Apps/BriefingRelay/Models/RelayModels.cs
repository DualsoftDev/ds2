// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
namespace BriefingRelay.Models;

/// <summary>DSPilot → 릴레이 발송 요청. From/제목/본문은 DSPilot 이 렌더한 것을 그대로 전달, 수신자는 서버가 Bcc 로 발송.</summary>
public sealed record SendRequest(string? Subject, string? Html, List<string>? Recipients);

/// <summary>발송 결과(DSPilot 의 BriefingSendResult 와 동일 형상 — camelCase).</summary>
public sealed record SendResponse(bool Sent, int RecipientCount, string Message);

/// <summary>릴레이 전체 설정. "Relay" 섹션 바인딩. Smtp/OAuth/ApiKeys 는 시크릿이라 주입(appsettings.Secrets.json/env).</summary>
public sealed class RelayConfig
{
    /// <summary>
    /// O365 인증 방식. "oauth"(권장) = Azure AD 앱 전용(client credentials) + Graph sendMail — 보안 기본값 환경에서도 동작.
    /// "basic" = SMTP 계정/비번(테넌트가 보안 기본값이면 535 로 막힘). 기본 basic(하위호환), 실배포는 oauth 권장.
    /// </summary>
    public string AuthMode { get; set; } = "basic";

    public RelaySmtp Smtp { get; set; } = new();

    public RelayOAuth OAuth { get; set; } = new();

    /// <summary>요청당 수신자 수 상한(남용·오배포 방어). 기본 100.</summary>
    public int MaxRecipientsPerRequest { get; set; } = 100;

    /// <summary>본문(HTML) 최대 바이트. 기본 1MB.</summary>
    public int MaxHtmlBytes { get; set; } = 1_048_576;

    /// <summary>허용 API 키 목록(설치별 1개). 비어 있으면 서버는 모든 요청을 거부(fail-closed).</summary>
    public List<ApiKeyEntry> ApiKeys { get; set; } = [];
}

/// <summary>회사 O365 발송 계정(서버에만 존재).</summary>
public sealed class RelaySmtp
{
    public string Host { get; set; } = "smtp.office365.com";
    public int Port { get; set; } = 587;
    public bool UseTls { get; set; } = true;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "DSPilot 브리핑";
}

/// <summary>Azure AD 앱 전용(client credentials) 인증 설정 — Graph sendMail 발송용. 보안 기본값/MFA 무관.</summary>
public sealed class RelayOAuth
{
    /// <summary>디렉터리(테넌트) ID.</summary>
    public string TenantId { get; set; } = "";

    /// <summary>애플리케이션(클라이언트) ID.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>클라이언트 비밀. 설치 시 주입 — 소스/커밋 금지.</summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>발송 주체 사서함(UPN 또는 오브젝트 ID). 예: help@yourcompany.com. Mail.Send 앱권한 + (권장)ApplicationAccessPolicy 로 이 사서함만 허용.</summary>
    public string Sender { get; set; } = "";

    /// <summary>발신자 표시명(Graph message.from.name).</summary>
    public string FromName { get; set; } = "DSPilot 브리핑";
}

/// <summary>설치 1건에 발급된 API 키 + 제약. 키가 곧 그 설치의 신원.</summary>
public sealed class ApiKeyEntry
{
    /// <summary>비밀 키 문자열(충분히 긴 랜덤). X-Api-Key 헤더로 대조.</summary>
    public string Key { get; set; } = "";

    /// <summary>식별용 이름(예: 고객사/현장). 로그·감사용.</summary>
    public string Name { get; set; } = "";

    /// <summary>이 키의 하루 발송(요청) 한도. 초과 시 429. 기본 200.</summary>
    public int DailyQuota { get; set; } = 200;

    /// <summary>허용 수신 도메인(예: ["yourcompany.com"]). 비어 있으면 도메인 제한 없음.</summary>
    public List<string> AllowedDomains { get; set; } = [];

    /// <summary>비활성화(즉시 차단) 플래그 — 키 유출 시 재발급 없이 끌 수 있게.</summary>
    public bool Disabled { get; set; } = false;
}
