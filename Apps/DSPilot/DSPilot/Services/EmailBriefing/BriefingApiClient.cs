// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DSPilot.Services.EmailBriefing;

/// <summary>중앙 발송 API(BriefingRelay) 호출 추상화 — 메일 자격증명 없이 제목/본문/수신자만 전달.</summary>
public interface IBriefingApiClient
{
    /// <summary>렌더된 브리핑을 중앙 API 로 발송 요청. 실패 시 예외(호출측이 결과화).</summary>
    Task SendAsync(string subject, string html, IReadOnlyList<string> recipients, CancellationToken ct);
}

public sealed class BriefingApiClient : IBriefingApiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly BriefingRelayOptions _relay;
    private readonly ILogger<BriefingApiClient> _logger;

    public BriefingApiClient(HttpClient http, BriefingRelayOptions relay, ILogger<BriefingApiClient> logger)
    {
        _http = http;
        _relay = relay;
        _logger = logger;
    }

    public async Task SendAsync(string subject, string html, IReadOnlyList<string> recipients, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_relay.ApiUrl))
            throw new InvalidOperationException("발송 API 주소(ApiUrl)가 구성되지 않았습니다.");
        if (string.IsNullOrWhiteSpace(_relay.ApiKey))
            throw new InvalidOperationException("발송 API 키(ApiKey)가 구성되지 않았습니다.");

        var url = _relay.ApiUrl.TrimEnd('/') + "/api/briefing/send";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new SendRequestDto(subject, html, recipients), options: Json)
        };
        req.Headers.TryAddWithoutValidation("X-Api-Key", _relay.ApiKey);

        using var res = await _http.SendAsync(req, ct);
        SendResponseDto? payload = null;
        try { payload = await res.Content.ReadFromJsonAsync<SendResponseDto>(Json, ct); }
        catch { /* 본문 파싱 실패는 상태코드로 판단 */ }

        if (res.IsSuccessStatusCode && payload is { Sent: true })
        {
            _logger.LogInformation("중앙 API 발송 성공 — 수신 {Count}명", payload.RecipientCount);
            return;
        }

        var msg = payload?.Message ?? $"HTTP {(int)res.StatusCode}";
        throw new InvalidOperationException($"중앙 발송 API 오류({(int)res.StatusCode}): {msg}");
    }

    private sealed record SendRequestDto(string Subject, string Html, IReadOnlyList<string> Recipients);
    private sealed record SendResponseDto(
        [property: JsonPropertyName("sent")] bool Sent,
        [property: JsonPropertyName("recipientCount")] int RecipientCount,
        [property: JsonPropertyName("message")] string? Message);
}
