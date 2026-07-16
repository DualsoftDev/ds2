// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Models;

namespace DSPilot.Services;

/// <summary>
/// 이 설치본의 <b>외부 접속 주소</b>(<see cref="ExternalAccessSettings.Url"/>) 유효값 해석기.
/// 서버는 자기 외부 주소(NAT 공인 IP·도메인·프록시)를 스스로 알 수 없으므로 값의 출처는 둘뿐이다:
///   ① 사용자 설정(설정 페이지 → Production.json) — 우선
///   ② 설치 시 주입(IConfiguration "ExternalAccess:Url" — appsettings.Secrets.json 또는 환경변수
///      <c>ExternalAccess__Url</c>) — 폴백. 클라우드 인스턴스 자동 생성(리눅스) 설치 스크립트가
///      인스턴스 공인 주소를 여기로 심는 용도(briefing-apikey → Secrets.json 주입과 같은 패턴).
/// 브리핑 메일 CTA 등 서버 밖으로 나가는 링크 생성처가 <see cref="ResolveUrl"/> 을 소비한다(빈 문자열 = 미설정).
/// </summary>
public sealed class ExternalAccessService
{
    private readonly AppSettingsService _settings;
    private readonly IConfiguration _config;

    public ExternalAccessService(AppSettingsService settings, IConfiguration config)
    {
        _settings = settings;
        _config = config;
    }

    /// <summary>유효 외부 접속 URL. 사용자 설정 ▸ 설치 주입값 순 폴백, 없거나 형식 오류면 "".</summary>
    public string ResolveUrl()
    {
        var user = Normalize(_settings.LoadSettings().ExternalAccess.Url);
        if (!string.IsNullOrEmpty(user)) return user!;
        return Normalize(SeedUrlRaw) ?? "";
    }

    /// <summary>설치 시 주입된 원시값(정규화 전). 비면 미주입. UI 의 "설치 시 지정됨" 안내용.</summary>
    public string SeedUrlRaw => _config["ExternalAccess:Url"] ?? "";

    /// <summary>
    /// 외부 접속 URL 정규화: 공백/끝 슬래시 제거, 스킴 생략 시 http:// 보정, 절대 http(s) URL 만 허용.
    /// 반환 — 정규화된 URL / ""(빈 입력 = 미설정) / null(형식 오류 — 저장 검증 실패용).
    /// </summary>
    public static string? Normalize(string? raw)
    {
        var v = (raw ?? "").Trim().TrimEnd('/');
        if (v.Length == 0) return "";
        if (!v.Contains("://", StringComparison.Ordinal)) v = "http://" + v;
        return Uri.TryCreate(v, UriKind.Absolute, out var u)
               && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
            ? v : null;
    }
}
