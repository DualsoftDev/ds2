// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Collections.Generic;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DSPilot.Services.CloudAuth;

/// <summary>
/// <see cref="IProvisioningAuthClient"/> 의 HttpClient 구현 — CloudWorks 프로비저닝 서버로 직접 프록시.
/// 서버 URL 은 <see cref="CloudAuthOptions.BaseUrl"/>(Secrets/환경변수 주입)에서만 읽는다.
/// 실패는 예외 대신 결과 레코드의 Ok=false + 메시지로 돌려 UI 가 그대로 표시하게 한다(BriefingApiClient 방침).
/// </summary>
public sealed class ProvisioningAuthClient : IProvisioningAuthClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly CloudAuthOptions _opts;
    private readonly ILogger<ProvisioningAuthClient> _logger;

    public ProvisioningAuthClient(HttpClient http, CloudAuthOptions opts, ILogger<ProvisioningAuthClient> logger)
    {
        _http = http;
        _opts = opts;
        _logger = logger;
    }

    public async Task<CloudAuthResult> RegisterAsync(
        string loginId, string password, string? displayName, string? companyName, CancellationToken ct)
    {
        if (!_opts.Configured)
            return CloudAuthResult.Fail("클라우드 서버 주소가 구성되지 않았습니다 (appsettings.Secrets.json 의 CloudAuth.BaseUrl).");

        var body = new RegisterDto(loginId, password, displayName, companyName);
        var (ok, json, err) = await PostAsync("/api/admin/register", body, ct);
        if (!ok) return CloudAuthResult.Fail(err!);

        var payload = TryParse<AuthResponseDto>(json);
        if (payload is null || string.IsNullOrEmpty(payload.AdminSession))
            return CloudAuthResult.Fail("가입 응답에 세션이 없습니다.");
        return CloudAuthResult.Success(payload.AdminId, payload.AdminSession, payload.DisplayName ?? displayName);
    }

    public async Task<CloudAuthResult> LoginAsync(string loginId, string password, CancellationToken ct)
    {
        if (!_opts.Configured)
            return CloudAuthResult.Fail("클라우드 서버 주소가 구성되지 않았습니다 (appsettings.Secrets.json 의 CloudAuth.BaseUrl).");

        var body = new LoginDto(loginId, password);
        var (ok, json, err) = await PostAsync("/api/admin/login", body, ct);
        if (!ok) return CloudAuthResult.Fail(err!);

        var payload = TryParse<AuthResponseDto>(json);
        if (payload is null || string.IsNullOrEmpty(payload.AdminSession))
            return CloudAuthResult.Fail("로그인 응답에 세션이 없습니다.");
        return CloudAuthResult.Success(payload.AdminId, payload.AdminSession, payload.DisplayName);
    }

    public async Task<CloudOverviewResult> OverviewAsync(string sessionToken, CancellationToken ct)
    {
        if (!_opts.Configured)
            return CloudOverviewResult.Fail("클라우드 서버 주소가 구성되지 않았습니다.");
        if (string.IsNullOrEmpty(sessionToken))
            return CloudOverviewResult.Fail("로그인이 필요합니다.");

        using var req = new HttpRequestMessage(HttpMethod.Get, Url("/api/account/overview"));
        req.Headers.TryAddWithoutValidation("X-Admin-Session", sessionToken);

        HttpResponseMessage res;
        try { res = await _http.SendAsync(req, ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "클라우드 overview 호출 실패");
            return CloudOverviewResult.Fail("클라우드 서버에 연결할 수 없습니다.");
        }

        var json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            return CloudOverviewResult.Fail(ExtractDetail(json) ?? $"조회 실패 (HTTP {(int)res.StatusCode}).");

        return ParseOverview(json);
    }

    // ── 공통 POST(JSON) — (성공, 본문, 오류메시지) ──
    private async Task<(bool ok, string? json, string? err)> PostAsync(string path, object body, CancellationToken ct)
    {
        HttpResponseMessage res;
        try
        {
            res = await _http.PostAsJsonAsync(Url(path), body, Json, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "클라우드 인증 호출 실패: {Path}", path);
            return (false, null, "클라우드 서버에 연결할 수 없습니다.");
        }

        var json = await res.Content.ReadAsStringAsync(ct);
        if (res.IsSuccessStatusCode)
            return (true, json, null);

        // FastAPI 는 오류를 {"detail": "..."} 로 준다.
        return (false, null, ExtractDetail(json) ?? $"요청 실패 (HTTP {(int)res.StatusCode}).");
    }

    private string Url(string path) => _opts.BaseUrl.TrimEnd('/') + path;

    private static T? TryParse<T>(string? json) where T : class
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, Json); }
        catch { return null; }
    }

    private static string? ExtractDetail(string? json)
    {
        var dto = TryParse<ErrorDto>(json);
        return string.IsNullOrWhiteSpace(dto?.Detail) ? null : dto!.Detail;
    }

    // /account/overview 원본 JSON → 표시용 트리. 파싱 실패해도 예외 대신 Fail.
    private static CloudOverviewResult ParseOverview(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? accountName = null;
            if (root.TryGetProperty("account", out var acc) && acc.ValueKind == JsonValueKind.Object)
                accountName = NullableStr(acc, "company_name") ?? NullableStr(acc, "display_name") ?? NullableStr(acc, "login_id");

            var sites = new List<CloudSite>();
            if (root.TryGetProperty("sites", out var sitesEl) && sitesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in sitesEl.EnumerateArray())
                {
                    var siteId = Str(s, "site_id");
                    var name = Str(s, "display_name");
                    if (string.IsNullOrEmpty(name)) name = siteId;

                    var edges = new List<CloudEdge>();
                    if (s.TryGetProperty("edges", out var edgesEl) && edgesEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var e in edgesEl.EnumerateArray())
                        {
                            string? instStatus = null, ip = null;
                            if (e.TryGetProperty("instance", out var inst) && inst.ValueKind == JsonValueKind.Object)
                            {
                                instStatus = NullableStr(inst, "status");
                                ip = NullableStr(inst, "public_ip");
                            }
                            edges.Add(new CloudEdge(Str(e, "edge_id"), Str(e, "status"), instStatus, ip));
                        }
                    }
                    sites.Add(new CloudSite(siteId, name, edges));
                }
            }
            return new CloudOverviewResult(true, accountName, sites, null);
        }
        catch (Exception ex)
        {
            return CloudOverviewResult.Fail($"응답 파싱 오류: {ex.Message}");
        }
    }

    private static string Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    private static string? NullableStr(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ── DTO ──
    private sealed record RegisterDto(string login_id, string password, string? display_name, string? company_name);
    private sealed record LoginDto(string login_id, string password);
    // 서버(admin.py)는 snake_case 로 응답 — 명시 매핑(Web camelCase 기본값과 불일치).
    private sealed record AuthResponseDto
    {
        [JsonPropertyName("admin_id")] public string? AdminId { get; init; }
        [JsonPropertyName("admin_session")] public string? AdminSession { get; init; }
        [JsonPropertyName("display_name")] public string? DisplayName { get; init; }
    }
    private sealed record ErrorDto { [JsonPropertyName("detail")] public string? Detail { get; init; } }
}
