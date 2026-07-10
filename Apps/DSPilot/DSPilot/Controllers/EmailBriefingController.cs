// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Globalization;
using DSPilot.Services;
using DSPilot.Services.EmailBriefing;
using Microsoft.AspNetCore.Mvc;
// (relay 고정 설정은 BriefingRelayOptions 로 주입 — 설치 시 자격증명 주입, git 미포함)

namespace DSPilot.Controllers;

/// <summary>
/// 일일 브리핑 메일 설정·발송 API. 정적 설정 페이지(settings-email.html)가 fetch 로 호출.
///   GET  api/email-briefing         : 설정 조회(SMTP 비밀번호는 마스킹 — 설정 여부만 노출)
///   POST api/email-briefing         : 설정 저장(비밀번호 빈 값이면 기존값 유지)
///   POST api/email-briefing/test    : 지금 즉시 테스트 발송(워터마크 미갱신 — 정규 발송 방해 안 함)
///   GET  api/email-briefing/preview : 발송될 HTML 본문 미리보기(발송 안 함)
/// camelCase 자동(MVC 기본값).
/// </summary>
[ApiController]
[Route("api/email-briefing")]
public class EmailBriefingController : ControllerBase
{
    private readonly AppSettingsService _settings;
    private readonly EmailBriefingService _briefing;
    private readonly BriefingRelayOptions _relay;

    public EmailBriefingController(AppSettingsService settings, EmailBriefingService briefing, BriefingRelayOptions relay)
    {
        _settings = settings;
        _briefing = briefing;
        _relay = relay;
    }

    [HttpGet]
    public ActionResult<EmailBriefingDto> Get()
    {
        var s = _settings.LoadSettings().EmailBriefing;
        return new EmailBriefingDto(
            Enabled: s.Enabled,
            Recipients: s.Recipients ?? [],
            SendTimeLocal: string.IsNullOrWhiteSpace(s.SendTimeLocal) ? "08:00" : s.SendTimeLocal,
            Weekdays: (s.Weekdays ?? []).OrderBy(x => x).ToList(),
            SendMode: string.Equals(s.SendMode, "direct", StringComparison.OrdinalIgnoreCase) ? "direct" : "relay",
            LastSentDate: s.LastSentDate ?? "",
            SmtpHost: s.SmtpHost ?? "",
            SmtpPort: s.SmtpPort,
            SmtpUseTls: s.SmtpUseTls,
            SmtpUser: s.SmtpUser ?? "",
            SmtpPasswordSet: !string.IsNullOrEmpty(s.SmtpPassword),
            FromAddress: s.FromAddress ?? "",
            FromName: string.IsNullOrWhiteSpace(s.FromName) ? "DSPilot 브리핑" : s.FromName,
            // 고정 릴레이(설치 시 주입) 상태 — UI 가 SMTP 카드 잠금/안내에 사용.
            RelayLocked: _relay.Locked,
            RelayCredentialConfigured: _relay.CredentialConfigured,
            RelayFrom: _relay.EffectiveFrom,
            RelayHost: _relay.Host,
            RelayMode: _relay.IsApiMode ? "api" : "smtp",
            // 표시용 엔드포인트(api=서버 URL, smtp=호스트). 비밀 아님.
            RelayEndpoint: _relay.IsApiMode ? _relay.ApiUrl : _relay.Host);
    }

    [HttpPost]
    public ActionResult<EmailBriefingDto> Save([FromBody] EmailBriefingSaveDto dto)
    {
        if (dto is null) return BadRequest("본문이 없습니다.");

        // 발송 시각 검증(HH:mm).
        var time = NormalizeTime(dto.SendTimeLocal);
        if (time is null) return BadRequest("발송 시각 형식이 올바르지 않습니다(HH:mm).");

        var recipients = (dto.Recipients ?? [])
            .Select(r => r?.Trim())
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(r => r!)
            .ToList();

        var weekdays = (dto.Weekdays ?? [])
            .Where(d => d is >= 0 and <= 6)
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        _settings.Update(m =>
        {
            var e = m.EmailBriefing;
            e.Enabled = dto.Enabled;
            e.Recipients = recipients;
            e.SendTimeLocal = time;
            e.Weekdays = weekdays;
            e.SendMode = string.Equals(dto.SendMode, "direct", StringComparison.OrdinalIgnoreCase) ? "direct" : "relay";
            e.SmtpHost = (dto.SmtpHost ?? "").Trim();
            e.SmtpPort = dto.SmtpPort is int p and > 0 and <= 65535 ? p : 587;
            e.SmtpUseTls = dto.SmtpUseTls ?? true;
            e.SmtpUser = (dto.SmtpUser ?? "").Trim();
            // 비밀번호: 값이 오면 교체, 빈 값/누락이면 기존값 보존(시크릿 편집 UX).
            if (!string.IsNullOrEmpty(dto.SmtpPassword))
                e.SmtpPassword = dto.SmtpPassword;
            e.FromAddress = (dto.FromAddress ?? "").Trim();
            e.FromName = string.IsNullOrWhiteSpace(dto.FromName) ? "DSPilot 브리핑" : dto.FromName.Trim();
        });

        return Get();
    }

    [HttpPost("test")]
    public async Task<ActionResult<BriefingSendResult>> Test(CancellationToken ct)
    {
        var result = await _briefing.SendNowAsync(updateWatermark: false, targetDay: null, ct);
        return result.Sent ? Ok(result) : StatusCode(502, result);
    }

    [HttpGet("preview")]
    public async Task<IActionResult> Preview(CancellationToken ct)
    {
        var preview = await _briefing.PreviewAsync(targetDay: null, ct);
        Response.Headers["X-Briefing-Subject"] = Uri.EscapeDataString(preview.Subject);
        return Content(preview.Html, "text/html; charset=utf-8");
    }

    private static string? NormalizeTime(string? hhmm)
    {
        if (string.IsNullOrWhiteSpace(hhmm)) return "08:00";
        if (TimeSpan.TryParseExact(hhmm, @"hh\:mm", CultureInfo.InvariantCulture, out var t)
            || TimeSpan.TryParse(hhmm, CultureInfo.InvariantCulture, out t))
        {
            if (t >= TimeSpan.Zero && t < TimeSpan.FromDays(1))
                return $"{t.Hours:D2}:{t.Minutes:D2}";
        }
        return null;
    }
}

// ── DTOs (camelCase 자동) ──
public record EmailBriefingDto(
    bool Enabled,
    List<string> Recipients,
    string SendTimeLocal,
    List<int> Weekdays,
    string SendMode,
    string LastSentDate,
    string SmtpHost,
    int SmtpPort,
    bool SmtpUseTls,
    string SmtpUser,
    bool SmtpPasswordSet,
    string FromAddress,
    string FromName,
    bool RelayLocked,
    bool RelayCredentialConfigured,
    string RelayFrom,
    string RelayHost,
    string RelayMode,
    string RelayEndpoint);

public record EmailBriefingSaveDto(
    bool Enabled,
    List<string>? Recipients,
    string? SendTimeLocal,
    List<int>? Weekdays,
    string? SendMode,
    string? SmtpHost,
    int? SmtpPort,
    bool? SmtpUseTls,
    string? SmtpUser,
    string? SmtpPassword,
    string? FromAddress,
    string? FromName);
