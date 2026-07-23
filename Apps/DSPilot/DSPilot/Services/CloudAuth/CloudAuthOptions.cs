// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
namespace DSPilot.Services.CloudAuth;

/// <summary>
/// CloudWorks(프로비저닝) 서버 연동 설정 — 회원가입/로그인 프록시가 호출할 베이스 URL.
///
/// <b>서버 주소는 public 소스(ds2)에 두지 않는다.</b> 설치 시 <c>appsettings.Secrets.json</c> 의
/// "CloudAuth" 섹션이나 환경변수로 주입한다(BriefingRelay 와 동일 방침). 엔드포인트 경로
/// (<c>/api/admin/login</c> 등)는 IP 가 아닌 서버 계약이라 코드에 둬도 무해(회색지대).
///
/// 주입 경로(둘 중 편한 것 — <see cref="Microsoft.Extensions.Configuration.IConfiguration"/> "CloudAuth" 섹션으로 병합):
///   ① 설치 스크립트가 배포 폴더에 <c>appsettings.Secrets.json</c> 배치:
///        { "CloudAuth": { "BaseUrl": "http://&lt;프로비저닝-서버&gt;" } }
///   ② 환경변수: <c>CloudAuth__BaseUrl</c>
/// </summary>
public sealed class CloudAuthOptions
{
    /// <summary>
    /// 프로비저닝 서버 베이스 URL(스킴+호스트[+포트]). 예: <c>http://211.x.x.x</c> · <c>https://cloud.example.com</c>.
    /// 경로(<c>/api/admin/login</c> 등)는 클라이언트가 붙인다. 비어 있으면 연동 API 가 "미구성" 오류를 반환한다.
    /// </summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>연동 사용 가능 여부(BaseUrl 주입됨).</summary>
    public bool Configured => !string.IsNullOrWhiteSpace(BaseUrl);
}
