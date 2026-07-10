// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using BriefingRelay.Models;
using BriefingRelay.Services;
using MimeKit;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSystemd(); // Linux 서비스로 기동됐을 때만 활성(그 외 no-op).

// 시크릿 주입 파일(자격증명·API키) — git/배포 소스 미포함. 없어도 기동은 되나 fail-closed.
// 환경변수(Relay__Smtp__Password, Relay__ApiKeys__0__Key 등)로도 주입 가능(CreateBuilder 가 env 병합).
builder.Configuration.AddJsonFile("appsettings.Secrets.json", optional: true, reloadOnChange: true);

var relayConfig = builder.Configuration.GetSection("Relay").Get<RelayConfig>() ?? new RelayConfig();
builder.Services.AddSingleton(relayConfig);
builder.Services.AddSingleton<ApiKeyStore>();
builder.Services.AddSingleton<RelayMailer>();
builder.Services.AddHttpClient(); // OAuth 토큰 + Graph sendMail 호출용 IHttpClientFactory

// 본문 크기 제한(대형 요청 방어) — HTML 상한 + 여유.
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = relayConfig.MaxHtmlBytes + 256 * 1024);

var app = builder.Build();

app.Logger.LogInformation(
    "BriefingRelay 시작 — 인증모드 {Auth}, 등록 키 {Keys}개, 발송계정 구성 {Cred}",
    relayConfig.AuthMode,
    relayConfig.ApiKeys?.Count(k => !string.IsNullOrWhiteSpace(k.Key)) ?? 0,
    app.Services.GetRequiredService<RelayMailer>().CredentialConfigured);

// 헬스체크(프록시/모니터링용) — 인증 불요, 상태만.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// ── 발송 엔드포인트 ──
app.MapPost("/api/briefing/send", async (
    HttpContext http,
    SendRequest? body,
    ApiKeyStore keys,
    RelayMailer mailer,
    RelayConfig cfg,
    ILoggerFactory lf,
    CancellationToken ct) =>
{
    var log = lf.CreateLogger("Send");

    // 1) 인증 먼저 — 미인증 호출자에게는 서버 구성 상태(키/계정 유무)를 노출하지 않는다(fail-closed).
    //    키가 하나도 없으면 Validate 가 항상 null → 모든 요청 401.
    var presented = http.Request.Headers["X-Api-Key"].ToString();
    var entry = keys.Validate(presented);
    if (entry is null)
    {
        log.LogWarning("발송 거부: 유효하지 않은 API 키 (from {Ip})", http.Connection.RemoteIpAddress);
        return Results.Json(new SendResponse(false, 0, "인증 실패."), statusCode: 401);
    }

    // 2) 서버 발송 준비 상태 — 인증된 호출자에게만 노출.
    if (!mailer.CredentialConfigured)
        return Results.Json(new SendResponse(false, 0, "서버 발송 계정이 구성되지 않았습니다."), statusCode: 503);

    // 2) 입력 검증.
    if (body is null || string.IsNullOrWhiteSpace(body.Subject) || string.IsNullOrWhiteSpace(body.Html))
        return Results.Json(new SendResponse(false, 0, "subject/html 이 필요합니다."), statusCode: 400);
    if (System.Text.Encoding.UTF8.GetByteCount(body.Html) > cfg.MaxHtmlBytes)
        return Results.Json(new SendResponse(false, 0, "본문이 너무 큽니다."), statusCode: 400);

    var recipients = (body.Recipients ?? [])
        .Select(r => r?.Trim())
        .Where(r => !string.IsNullOrWhiteSpace(r))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(r => r!)
        .ToList();
    if (recipients.Count == 0)
        return Results.Json(new SendResponse(false, 0, "수신자가 없습니다."), statusCode: 400);
    if (recipients.Count > cfg.MaxRecipientsPerRequest)
        return Results.Json(new SendResponse(false, 0, $"수신자가 상한({cfg.MaxRecipientsPerRequest})을 초과했습니다."), statusCode: 400);

    // 유효 주소 + (설정 시) 도메인 화이트리스트.
    foreach (var r in recipients)
    {
        // MimeKit 파서가 관대해 도메인 없는 문자열도 통과할 수 있어, @ + 도메인 존재를 명시 검증.
        var at = MailboxAddress.TryParse(r, out var addr) ? addr.Address.LastIndexOf('@') : -1;
        if (addr is null || at <= 0 || at >= addr.Address.Length - 1)
            return Results.Json(new SendResponse(false, 0, $"주소 형식 오류: {r}"), statusCode: 400);
        if (entry.AllowedDomains is { Count: > 0 })
        {
            var domain = addr.Address[(at + 1)..];
            if (!entry.AllowedDomains.Any(d => string.Equals(d, domain, StringComparison.OrdinalIgnoreCase)))
                return Results.Json(new SendResponse(false, 0, $"허용되지 않은 수신 도메인: {domain}"), statusCode: 403);
        }
    }

    // 3) 쿼터.
    if (!keys.TryConsume(entry, recipients.Count))
    {
        log.LogWarning("발송 거부: 쿼터 초과 — 키 '{Name}'", entry.Name);
        return Results.Json(new SendResponse(false, 0, "일일 발송 한도를 초과했습니다."), statusCode: 429);
    }

    // 4) 발송.
    try
    {
        await mailer.SendAsync(body.Subject!, body.Html!, recipients, ct);
        log.LogInformation("발송 성공 — 키 '{Name}', 수신 {Count}명", entry.Name, recipients.Count);
        return Results.Ok(new SendResponse(true, recipients.Count, $"{recipients.Count}명에게 발송했습니다."));
    }
    catch (Exception ex)
    {
        log.LogError(ex, "발송 실패 — 키 '{Name}'", entry.Name);
        return Results.Json(new SendResponse(false, recipients.Count, "발송 실패: " + ex.Message), statusCode: 502);
    }
});

app.Run();
