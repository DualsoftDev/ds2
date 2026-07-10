// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BriefingRelay.Models;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace BriefingRelay.Services;

/// <summary>
/// 실제 발송. AuthMode 에 따라:
///   "oauth"(권장) = Azure AD 앱 전용 토큰(client credentials) → Microsoft Graph sendMail. 보안 기본값/MFA 무관.
///   "basic"       = O365 SMTP 계정/비번(MailKit). 테넌트 보안 기본값이면 535 로 막힘.
/// From 은 서버 설정(Sender/발신주소)으로 고정 — 호출자는 제목/본문/수신자만.
/// </summary>
public sealed class RelayMailer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly RelayConfig _cfg;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<RelayMailer> _logger;

    // 앱 전용 토큰 캐시(만료 전까지 재사용).
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;

    public RelayMailer(RelayConfig cfg, IHttpClientFactory httpFactory, ILogger<RelayMailer> logger)
    {
        _cfg = cfg;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    private bool IsOAuth => string.Equals(_cfg.AuthMode, "oauth", StringComparison.OrdinalIgnoreCase);

    /// <summary>발송 준비(자격증명) 완료 여부.</summary>
    public bool CredentialConfigured => IsOAuth
        ? !string.IsNullOrWhiteSpace(_cfg.OAuth.TenantId)
          && !string.IsNullOrWhiteSpace(_cfg.OAuth.ClientId)
          && !string.IsNullOrWhiteSpace(_cfg.OAuth.ClientSecret)
          && !string.IsNullOrWhiteSpace(_cfg.OAuth.Sender)
        : !string.IsNullOrWhiteSpace(_cfg.Smtp.Host)
          && !string.IsNullOrWhiteSpace(_cfg.Smtp.User)
          && !string.IsNullOrWhiteSpace(_cfg.Smtp.Password);

    public Task SendAsync(string subject, string htmlBody, IReadOnlyList<string> recipients, CancellationToken ct)
        => IsOAuth
            ? SendViaGraphAsync(subject, htmlBody, recipients, ct)
            : SendViaSmtpAsync(subject, htmlBody, recipients, ct);

    // ── OAuth 앱 전용: Graph sendMail ──
    private async Task SendViaGraphAsync(string subject, string htmlBody, IReadOnlyList<string> recipients, CancellationToken ct)
    {
        var o = _cfg.OAuth;
        var token = await GetAppTokenAsync(ct);

        // Graph message: 수신자는 bcc(상호 비노출), from 은 Sender 로 고정.
        var message = new
        {
            message = new
            {
                subject,
                body = new { contentType = "HTML", content = htmlBody },
                from = new { emailAddress = new { address = o.Sender, name = o.FromName } },
                bccRecipients = recipients.Select(r => new { emailAddress = new { address = r } }).ToArray(),
            },
            saveToSentItems = false,
        };

        var http = _httpFactory.CreateClient();
        var url = $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(o.Sender)}/sendMail";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(message, options: Json)
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode == System.Net.HttpStatusCode.Accepted) // 202 = 성공
        {
            _logger.LogInformation("Graph 발송 완료 — 수신 {Count}명", recipients.Count);
            return;
        }
        var detail = await res.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException($"Graph sendMail 실패({(int)res.StatusCode}): {Truncate(detail, 400)}");
    }

    // client_credentials 토큰 취득(캐시). scope=graph/.default.
    private async Task<string> GetAppTokenAsync(CancellationToken ct)
    {
        // 만료 60초 여유.
        if (_cachedToken is not null && DateTimeOffset.UtcNow < _tokenExpiresAt.AddSeconds(-60))
            return _cachedToken;

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_cachedToken is not null && DateTimeOffset.UtcNow < _tokenExpiresAt.AddSeconds(-60))
                return _cachedToken;

            var o = _cfg.OAuth;
            var http = _httpFactory.CreateClient();
            var tokenUrl = $"https://login.microsoftonline.com/{o.TenantId}/oauth2/v2.0/token";
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = o.ClientId,
                ["client_secret"] = o.ClientSecret,
                ["scope"] = "https://graph.microsoft.com/.default",
                ["grant_type"] = "client_credentials",
            });
            using var res = await http.PostAsync(tokenUrl, form, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException($"토큰 취득 실패({(int)res.StatusCode}): {Truncate(body, 400)}");

            var tok = JsonSerializer.Deserialize<TokenResponse>(body, Json)
                      ?? throw new InvalidOperationException("토큰 응답 파싱 실패");
            if (string.IsNullOrEmpty(tok.AccessToken))
                throw new InvalidOperationException("access_token 이 비어 있음");

            _cachedToken = tok.AccessToken;
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tok.ExpiresIn > 0 ? tok.ExpiresIn : 3600);
            return _cachedToken;
        }
        finally { _tokenLock.Release(); }
    }

    // ── basic: SMTP(MailKit) ──
    private async Task SendViaSmtpAsync(string subject, string htmlBody, IReadOnlyList<string> recipients, CancellationToken ct)
    {
        var smtp = _cfg.Smtp;
        var from = string.IsNullOrWhiteSpace(smtp.FromAddress) ? smtp.User : smtp.FromAddress;

        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(string.IsNullOrWhiteSpace(smtp.FromName) ? "DSPilot" : smtp.FromName, from));
        msg.To.Add(new MailboxAddress(string.Empty, from)); // 실제 수신자는 Bcc
        foreach (var r in recipients)
            msg.Bcc.Add(MailboxAddress.Parse(r));
        msg.Subject = subject;
        msg.Body = new BodyBuilder { HtmlBody = htmlBody }.ToMessageBody();

        var socket = !smtp.UseTls
            ? SecureSocketOptions.None
            : smtp.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;

        using var client = new SmtpClient();
        await client.ConnectAsync(smtp.Host, smtp.Port, socket, ct);
        if (!string.IsNullOrWhiteSpace(smtp.User))
            await client.AuthenticateAsync(smtp.User, smtp.Password ?? string.Empty, ct);
        await client.SendAsync(msg, ct);
        await client.DisconnectAsync(true, ct);

        _logger.LogInformation("SMTP 발송 완료 — 수신 {Count}명", recipients.Count);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
