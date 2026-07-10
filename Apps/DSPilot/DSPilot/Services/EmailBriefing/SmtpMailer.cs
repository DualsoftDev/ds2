// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Models;
using DnsClient;
using DnsClient.Protocol;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace DSPilot.Services.EmailBriefing;

/// <summary>SMTP 발송 추상화 — 브리핑 서비스와 발송 구현을 분리(테스트/대체 용이).</summary>
public interface ISmtpMailer
{
    /// <summary>
    /// HTML 메일 1통 발송. 발송 모드(<see cref="EmailBriefingSettings.SendMode"/>)에 따라:
    ///   relay  = 지정 SMTP 서버로 위임(수신자 Bcc — 서로 주소 비노출).
    ///   direct = 수신 도메인별 MX 로 DSPilot 이 직접 발송(중계 서버 없음).
    /// SMTP/발송 설정은 <paramref name="cfg"/> 에서 읽는다. 실패 시 예외를 던진다(호출측이 잡아 결과화).
    /// </summary>
    Task SendHtmlAsync(EmailBriefingSettings cfg, string subject, string htmlBody, CancellationToken ct);
}

public sealed class SmtpMailer : ISmtpMailer
{
    private readonly ILogger<SmtpMailer> _logger;
    // LookupClient 는 스레드 안전·재사용 권장. 시스템 DNS 서버 사용.
    private static readonly LookupClient Dns = new();

    public SmtpMailer(ILogger<SmtpMailer> logger) => _logger = logger;

    public async Task SendHtmlAsync(EmailBriefingSettings cfg, string subject, string htmlBody, CancellationToken ct)
    {
        var recipients = NormalizeRecipients(cfg.Recipients);
        if (recipients.Count == 0)
            throw new InvalidOperationException("수신 메일 주소가 없습니다.");

        var fromAddress = string.IsNullOrWhiteSpace(cfg.FromAddress) ? cfg.SmtpUser : cfg.FromAddress;
        if (string.IsNullOrWhiteSpace(fromAddress))
            throw new InvalidOperationException("발신 주소(From) 또는 SMTP 계정이 설정되지 않았습니다.");

        if (string.Equals(cfg.SendMode, "direct", StringComparison.OrdinalIgnoreCase))
            await SendDirectAsync(cfg, fromAddress, recipients, subject, htmlBody, ct);
        else
            await SendViaRelayAsync(cfg, fromAddress, recipients, subject, htmlBody, ct);
    }

    // ── relay: 지정 SMTP 서버로 위임(권장) ──
    private async Task SendViaRelayAsync(
        EmailBriefingSettings cfg, string fromAddress, List<string> recipients,
        string subject, string htmlBody, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.SmtpHost))
            throw new InvalidOperationException("SMTP 호스트가 설정되지 않았습니다.");

        // 수신자는 Bcc(To=발신 자신) — 상호 주소 노출 방지.
        var msg = BuildMessage(cfg, fromAddress, recipients, subject, htmlBody);

        var socketOptions = !cfg.SmtpUseTls
            ? SecureSocketOptions.None
            : cfg.SmtpPort == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;

        using var client = new SmtpClient();
        await client.ConnectAsync(cfg.SmtpHost, cfg.SmtpPort, socketOptions, ct);
        if (!string.IsNullOrWhiteSpace(cfg.SmtpUser))
            await client.AuthenticateAsync(cfg.SmtpUser, cfg.SmtpPassword ?? string.Empty, ct);
        await client.SendAsync(msg, ct);
        await client.DisconnectAsync(true, ct);

        _logger.LogInformation("브리핑 메일 발송 완료(relay) — 수신 {Count}명", recipients.Count);
    }

    // ── direct-to-MX: 중계 서버 없이 수신 도메인 MX 로 직접 발송(비권장) ──
    private async Task SendDirectAsync(
        EmailBriefingSettings cfg, string fromAddress, List<string> recipients,
        string subject, string htmlBody, CancellationToken ct)
    {
        var atIdx = fromAddress.LastIndexOf('@');
        if (atIdx <= 0 || atIdx == fromAddress.Length - 1)
            throw new InvalidOperationException("direct 발송에는 도메인이 포함된 발신 주소(From)가 필요합니다.");
        var heloDomain = fromAddress[(atIdx + 1)..].Trim();

        // 수신자를 도메인별로 묶어 각 도메인의 MX 로 발송.
        var byDomain = recipients
            .GroupBy(r => r[(r.LastIndexOf('@') + 1)..].ToLowerInvariant())
            .ToList();

        var failures = new List<string>();
        var sentCount = 0;

        foreach (var group in byDomain)
        {
            ct.ThrowIfCancellationRequested();
            var domain = group.Key;
            var domainRecipients = group.ToList();

            List<string> mxHosts;
            try { mxHosts = await ResolveMxAsync(domain, ct); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MX 조회 실패: {Domain}", domain);
                failures.Add($"{domain}(MX 조회 실패)");
                continue;
            }
            if (mxHosts.Count == 0) { failures.Add($"{domain}(MX 없음)"); continue; }

            var msg = BuildMessage(cfg, fromAddress, domainRecipients, subject, htmlBody);
            var delivered = false;
            Exception? last = null;

            foreach (var mx in mxHosts)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var client = new SmtpClient { LocalDomain = heloDomain };
                    // 상대 서버가 STARTTLS 를 광고하면 사용, 아니면 평문. 무인증.
                    await client.ConnectAsync(mx, 25, SecureSocketOptions.StartTlsWhenAvailable, ct);
                    await client.SendAsync(msg, ct);
                    await client.DisconnectAsync(true, ct);
                    delivered = true;
                    _logger.LogInformation("direct 발송 성공 — {Domain} via {Mx} ({N}명)", domain, mx, domainRecipients.Count);
                    break;
                }
                catch (Exception ex)
                {
                    last = ex;
                    _logger.LogWarning(ex, "direct 발송 시도 실패 — {Domain} via {Mx}", domain, mx);
                }
            }

            if (delivered) sentCount += domainRecipients.Count;
            else failures.Add($"{domain}({last?.Message ?? "발송 실패"})");
        }

        if (failures.Count > 0)
        {
            var detail = string.Join("; ", failures);
            if (sentCount == 0)
                throw new InvalidOperationException($"direct 발송 실패 — {detail} (포트25 차단·SPF/DKIM 부재로 스팸/반송 가능성. 릴레이 모드 권장.)");
            _logger.LogWarning("direct 발송 부분 실패 — 성공 {Sent}명, 실패: {Detail}", sentCount, detail);
        }
    }

    // 수신 도메인의 MX 호스트(선호도 오름차순). MX 없으면 도메인 자체(A/AAAA)로 폴백(RFC 5321).
    private static async Task<List<string>> ResolveMxAsync(string domain, CancellationToken ct)
    {
        var result = await Dns.QueryAsync(domain, QueryType.MX, cancellationToken: ct);
        var mx = result.Answers.OfType<MxRecord>()
            .OrderBy(m => m.Preference)
            .Select(m => m.Exchange.Value.TrimEnd('.'))
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .ToList();
        if (mx.Count == 0) mx.Add(domain); // implicit MX 폴백
        return mx;
    }

    // To = 발신 자신, 실제 수신자는 Bcc — 릴레이/다이렉트 공통(도메인 그룹 단위로 호출).
    private static MimeMessage BuildMessage(
        EmailBriefingSettings cfg, string fromAddress, IEnumerable<string> recipients,
        string subject, string htmlBody)
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(
            string.IsNullOrWhiteSpace(cfg.FromName) ? "DSPilot" : cfg.FromName, fromAddress));
        msg.To.Add(new MailboxAddress(string.Empty, fromAddress));
        foreach (var r in recipients)
            msg.Bcc.Add(MailboxAddress.Parse(r));
        msg.Subject = subject;
        msg.Body = new BodyBuilder { HtmlBody = htmlBody }.ToMessageBody();
        return msg;
    }

    private static List<string> NormalizeRecipients(IEnumerable<string>? recipients) =>
        (recipients ?? [])
            .Select(r => r?.Trim())
            .Where(r => !string.IsNullOrWhiteSpace(r) && r!.Contains('@'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(r => r!)
            .ToList();
}
