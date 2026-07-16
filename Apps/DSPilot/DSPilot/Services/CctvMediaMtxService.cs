// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DSPilot.Models;

namespace DSPilot.Services;

/// <summary>
/// DSPilot 의 CCTV 카메라 목록(appsettings 의 Cctv 섹션)을 별도 프로세스인 MediaMTX 의
/// 제어 API(:9997) 로 동기화한다. MediaMTX 가 RTSP 를 받아 WebRTC 로 재게시하면
/// /cctv 페이지가 브라우저에서 시청한다.
///
/// - 시작 시: MediaMTX 가 아직 안 떠 있을 수 있으므로(서비스 부팅 순서 무관) 재시도/백오프 후 동기화.
/// - 저장 시: Settings 페이지가 <see cref="SyncAsync"/> 를 직접 호출.
///
/// MediaMTX 는 CCTV 전용이라고 가정하고, 설정에 없는 경로는 제거(reconcile)한다.
/// </summary>
public class CctvMediaMtxService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly AppSettingsService _settings;
    private readonly ExternalAccessService _externalAccess;
    private readonly ILogger<CctvMediaMtxService> _logger;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool LastSyncOk { get; private set; }
    public string LastSyncMessage { get; private set; } = "동기화 전";
    public DateTime? LastSyncUtc { get; private set; }

    public CctvMediaMtxService(AppSettingsService settings, ExternalAccessService externalAccess, ILogger<CctvMediaMtxService> logger)
    {
        _settings = settings;
        _externalAccess = externalAccess;
        _logger = logger;
    }

    /// <summary>실패 시 빠른 재시도(부팅 순서 무관). 성공 시 이 간격으로 주기 재동기화.</summary>
    private static readonly TimeSpan HealthyInterval = TimeSpan.FromSeconds(30);

    /// <summary>WebRTC 미디어 TCP 폴백 리스너 주소(UDP 8189 와 동일 포트, 프로토콜만 다름).
    /// UDP 차단망(모바일/사내/일부 클라우드) 대비. 공인주소가 설정된 경우에만 함께 켠다.</summary>
    private const string WebRtcTcpFallbackAddress = ":8189";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 주기 재동기화: MediaMTX 가 우리보다 늦게 떠도, 또는 독립적으로 재시작돼
        // 런타임 경로가 초기화돼도 다음 주기에 자동 복구된다(reconcile).
        var backoff = TimeSpan.FromSeconds(2);
        var maxBackoff = TimeSpan.FromSeconds(30);

        while (!stoppingToken.IsCancellationRequested)
        {
            var ok = await SyncAsync(stoppingToken);

            TimeSpan wait;
            if (ok)
            {
                wait = HealthyInterval;
                backoff = TimeSpan.FromSeconds(2);
            }
            else
            {
                wait = backoff;
                backoff = TimeSpan.FromSeconds(Math.Min(maxBackoff.TotalSeconds, backoff.TotalSeconds * 2));
            }

            try { await Task.Delay(wait, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// 현재 카메라 설정을 MediaMTX 경로 구성에 반영. 성공하면 true.
    /// Settings 저장 후, 시작 시 재시도 루프에서 호출된다.
    /// </summary>
    public async Task<bool> SyncAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var cctv = _settings.LoadSettings().Cctv;
            var apiBase = cctv.MediaMtxApiUrl.TrimEnd('/');

            // 경로명(slug)을 결정한다 — 저장 안 된 구(舊)/수기편집 설정도 동일 규칙으로 채워(deterministic)
            // GetConfig 가 내려보내는 slug 와 항상 일치하게 한다. 저장 경로(SaveSettings)는 이를 영속화한다.
            AssignSlugs(cctv.Cameras);

            var desired = cctv.Cameras
                .Where(c => c.Enabled && !string.IsNullOrWhiteSpace(c.Slug) && !string.IsNullOrWhiteSpace(c.RtspUrl))
                .GroupBy(c => c.Slug, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var existing = await GetConfiguredPathNamesAsync(apiBase, ct);

            // 추가/갱신 (경로명 = slug). 매 동기화마다 현재 source 를 그대로 patch 한다 — RtspUrl 만 바꿔
            // (slug 동일) 저장해도 새 주소가 반영된다. MediaMTX 는 config 가 동일하면 reload 를 생략하므로
            // 변화 없는 카메라는 patch 해도 스트림이 끊기지 않고, 주소가 달라진 카메라만 재연결된다.
            foreach (var (slug, cam) in desired)
            {
                var body = JsonSerializer.Serialize(
                    new PathConfig { Source = NormalizeRtspSourceUrl(cam.RtspUrl), SourceOnDemand = true }, JsonOptions);
                var verb = existing.Contains(slug) ? "patch" : "add";
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                var res = await _http.PostAsync($"{apiBase}/v3/config/paths/{verb}/{Uri.EscapeDataString(slug)}", content, ct);
                if (!res.IsSuccessStatusCode)
                    _logger.LogWarning("[CCTV] MediaMTX 경로 {Verb} 실패 {Slug}: {Status}", verb, slug, res.StatusCode);
            }

            // 설정에 없는 경로 제거 (MediaMTX 는 CCTV 전용 가정)
            foreach (var slug in existing.Where(n => !desired.ContainsKey(n)))
            {
                var res = await _http.DeleteAsync($"{apiBase}/v3/config/paths/delete/{Uri.EscapeDataString(slug)}", ct);
                if (!res.IsSuccessStatusCode)
                    _logger.LogWarning("[CCTV] MediaMTX 경로 삭제 실패 {Slug}: {Status}", slug, res.StatusCode);
            }

            // 외부(원격·클라우드) 접속용 전역 WebRTC 설정 반영(공인주소 광고 + TCP 폴백). best-effort.
            await SyncGlobalWebRtcAsync(apiBase, cctv, ct);

            LastSyncOk = true;
            LastSyncUtc = DateTime.UtcNow;
            LastSyncMessage = $"카메라 {desired.Count}대 동기화 완료";
            _logger.LogInformation("[CCTV] MediaMTX 동기화 완료 — 카메라 {Count}대", desired.Count);
            return true;
        }
        catch (Exception ex)
        {
            LastSyncOk = false;
            LastSyncUtc = DateTime.UtcNow;
            LastSyncMessage = $"MediaMTX 연결 실패: {ex.Message}";
            _logger.LogWarning(ex, "[CCTV] MediaMTX 동기화 실패 (서비스 미기동/네트워크?)");
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HashSet<string>> GetConfiguredPathNamesAsync(string apiBase, CancellationToken ct)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 페이지네이션: itemsPerPage 크게 잡아 한 번에. CCTV 경로 수는 적다.
        var json = await _http.GetStringAsync($"{apiBase}/v3/config/paths/list?itemsPerPage=1000", ct);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name)
                    names.Add(name);
            }
        }
        return names;
    }

    /// <summary>
    /// 외부 접속용 전역 WebRTC 설정을 MediaMTX 에 반영한다.
    /// 공인주소 = 전역 외부 접속 주소(ExternalAccess.Url)의 host ∪ CCTV 잔존값(WebRtcAdditionalHosts) <b>합집합</b>
    /// (2026-07-16). CCTV UI 입력은 제거됨 — 사용자는 전역 한 곳만 입력하고, 구버전 설치본에 저장돼 있던 CCTV 값도
    /// 계속 광고돼 무중단(잔존값이 전역을 가리는 숨은 우선값이 되지 않도록 폴백이 아닌 합집합. ICE 후보는 여러 host
    /// 광고해도 무해 — 브라우저가 닿는 것만 쓴다). 둘 다 비면 <b>무동작</b> — LAN 전용 의도 보존.
    /// 값이 있으면: ① webrtcAdditionalHosts 에 공인 IP/도메인을 광고(클라우드 VM 은 NIC 사설 IP 만 광고돼
    /// 외부 브라우저가 미디어에 못 닿는 문제 해결), ② UDP 차단망 대비 TCP 폴백(webrtcLocalTCPAddress)도 동반.
    /// 매 reconcile 마다 GET 으로 현재값과 비교해 <b>다를 때만</b> patch — 불필요한 WebRTC 서버 reload(스트림 끊김) 회피.
    /// MediaMTX 가 독립 재시작돼 yml 기본값으로 돌아가도 다음 주기에 다시 맞춘다(경로 reconcile 과 동일 자가복구).
    /// 전역 patch 실패는 비치명(경로 동기화 성공은 유지) — 경고만 남긴다.
    /// </summary>
    private async Task SyncGlobalWebRtcAsync(string apiBase, CctvSettings cctv, CancellationToken ct)
    {
        var desiredHosts = (cctv.WebRtcAdditionalHosts ?? "")
            .Split(new[] { ',', ';', ' ', '\t', '\r', '\n' },
                   StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        // 전역 외부 접속 주소의 host 를 합집합에 추가(스킴·포트 제외 — ICE 광고는 host 만).
        if (_externalAccess.ResolveUrl() is { Length: > 0 } externalUrl
            && Uri.TryCreate(externalUrl, UriKind.Absolute, out var eu))
        {
            desiredHosts.Add(eu.Host);
        }

        desiredHosts = desiredHosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // 공인주소 미설정(전역·CCTV 잔존값 모두 빈 값) = LAN 전용 의도 → 전역 설정에 일절 손대지 않는다.
        if (desiredHosts.Count == 0) return;

        try
        {
            var json = await _http.GetStringAsync($"{apiBase}/v3/config/global/get", ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var currentHosts = new List<string>();
            if (root.TryGetProperty("webrtcAdditionalHosts", out var ha) && ha.ValueKind == JsonValueKind.Array)
                foreach (var h in ha.EnumerateArray())
                    if (h.GetString() is { Length: > 0 } s) currentHosts.Add(s);

            var currentTcp = root.TryGetProperty("webrtcLocalTCPAddress", out var ta)
                             && ta.ValueKind == JsonValueKind.String ? (ta.GetString() ?? "") : "";

            // 순서 무관 비교(광고 순서는 무의미) — 순서만 다른데 매 주기 patch 하면 reload 가 반복돼 스트림이 끊긴다.
            var hostsMatch = currentHosts.Count == desiredHosts.Count
                && new HashSet<string>(currentHosts, StringComparer.OrdinalIgnoreCase).SetEquals(desiredHosts);
            var tcpMatch = string.Equals(currentTcp, WebRtcTcpFallbackAddress, StringComparison.OrdinalIgnoreCase);
            if (hostsMatch && tcpMatch) return; // 이미 원하는 상태 — patch 생략(reload 회피)

            var patch = JsonSerializer.Serialize(
                new { webrtcAdditionalHosts = desiredHosts, webrtcLocalTCPAddress = WebRtcTcpFallbackAddress }, JsonOptions);
            using var content = new StringContent(patch, Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(new HttpMethod("PATCH"), $"{apiBase}/v3/config/global/patch") { Content = content };
            var res = await _http.SendAsync(req, ct);
            if (res.IsSuccessStatusCode)
                _logger.LogInformation("[CCTV] MediaMTX 전역 WebRTC 반영 — 공인주소 [{Hosts}] + TCP 폴백 {Tcp}",
                    string.Join(", ", desiredHosts), WebRtcTcpFallbackAddress);
            else
                _logger.LogWarning("[CCTV] MediaMTX 전역 WebRTC patch 실패: {Status}", res.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CCTV] MediaMTX 전역 WebRTC 동기화 실패 (비치명)");
        }
    }

    /// <summary>authority 를 끝내는 구분자 — 이 뒤는 path/query/fragment 라 userinfo 인코딩 대상이 아니다.</summary>
    private static readonly char[] AuthorityTerminators = { '/', '?', '#' };

    /// <summary>
    /// RTSP 소스 URL 의 userinfo(아이디:비밀번호) 구간에 든 특수문자를 percent-encode 한다.
    /// 비밀번호에 `@` 같은 예약문자가 그대로 들어오면 MediaMTX(Go net/url)가 "invalid userinfo" 로
    /// 거부해 카메라 연결이 실패하는데, 사용자가 직접 인코딩하지 않아도 되게 여기서 흡수한다.
    /// - authority 의 <b>마지막</b> `@` 가 userinfo/호스트 구분자(Go net/url 동작과 동일) — 이건 보존.
    ///   (구분자 `@` 까지 `%40` 으로 바꾸면 호스트 경계가 사라져 오히려 파싱 실패한다.)
    /// - userinfo 안의 RFC 3986 허용문자와 이미 인코딩된 `%XX` 는 그대로 둬서, `dual%40soft` 처럼
    ///   기 인코딩된 입력이 이중 인코딩되지 않는다(멱등).
    /// - 비밀번호에 `/`·`?`·`#` 가 들어가 authority 가 조기 종결된 경우도 복구한다: 조기 종결되면
    ///   파싱된 authority 가 비숫자 포트(`u:p`) 등 <b>유효하지 않은</b> host:port 가 되므로, 그때만
    ///   전체 문자열의 마지막 `@` 를 구분자로 재해석해 앞부분을 통째로 인코딩한다. 복구는 그대로는
    ///   어차피 접속 불가능한 URL 에서만 발동하므로 멀쩡한 URL 이 훼손될 일은 없다.
    ///   잔여 한계: 비밀번호의 구분자 앞부분이 순수 숫자라 우연히 유효한 포트로 읽히는 경우
    ///   (예: `u:554/ab@host`)는 깨진 걸 감지할 수 없어 사용자가 직접 `%2F` 로 입력해야 한다.
    /// </summary>
    public static string NormalizeRtspSourceUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) return url;
        var authStart = schemeEnd + 3;
        var authEnd = url.IndexOfAny(AuthorityTerminators, authStart);
        if (authEnd < 0) authEnd = url.Length;
        var authority = url[authStart..authEnd];
        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            var userinfo = authority[..at];
            var encoded = EncodeUserinfo(userinfo);
            return encoded == userinfo ? url : url[..authStart] + encoded + url[(authStart + at)..];
        }
        // authority 에 @ 없음 — 무자격증명 URL 이거나, 비밀번호 속 '/'·'?'·'#' 가 authority 를 조기 종결.
        // 전자는 host[:port] 가 유효하므로 그대로 두고, 유효하지 않을 때만(=그대로는 접속 불가) 복구 시도.
        if (IsValidHostPort(authority)) return url;
        return TryRecoverTerminatorInPassword(url, authStart);
    }

    /// <summary>
    /// 비밀번호 속 `/`·`?`·`#` 로 authority 가 조기 종결돼 깨진 URL 의 best-effort 복구.
    /// 전체 잔여 문자열의 <b>마지막</b> `@` 뒤가 유효한 host[:port] 일 때만, 앞부분 전체를
    /// userinfo 로 간주해 인코딩한다(이때 `/`·`?`·`#`·`@` 가 전부 percent-encode 된다).
    /// 확신이 없으면(유효한 호스트 후보 없음) 원문 그대로 반환.
    /// </summary>
    private static string TryRecoverTerminatorInPassword(string url, int authStart)
    {
        var rest = url[authStart..];
        var at = rest.LastIndexOf('@');
        if (at < 0) return url;
        var afterAt = rest[(at + 1)..];
        var hostEnd = afterAt.IndexOfAny(AuthorityTerminators);
        var hostPort = hostEnd < 0 ? afterAt : afterAt[..hostEnd];
        if (!IsValidHostPort(hostPort)) return url;
        return url[..authStart] + EncodeUserinfo(rest[..at]) + rest[at..];
    }

    /// <summary>
    /// `host[:port]` 형태로 유효한지 — 호스트 비어있지 않음 + 포트는 숫자(1~65535)만 허용.
    /// 호스트 문자 자체는 관대하게 본다(언더스코어 등 비표준 호스트명을 깨진 것으로 오판해
    /// 멀쩡한 URL 을 복구 모드로 보내지 않기 위함). 비숫자 포트가 깨짐 감지의 핵심 신호다.
    /// </summary>
    private static bool IsValidHostPort(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        string host;
        string? port = null;
        if (s[0] == '[') // IPv6 리터럴
        {
            var close = s.IndexOf(']');
            if (close < 0) return false;
            host = s[..(close + 1)];
            var tail = s[(close + 1)..];
            if (tail.Length > 0)
            {
                if (tail[0] != ':') return false;
                port = tail[1..];
            }
        }
        else
        {
            var colon = s.LastIndexOf(':');
            if (colon >= 0) { host = s[..colon]; port = s[(colon + 1)..]; }
            else host = s;
            if (host.Contains(':') || host.Contains('[') || host.Contains(']')) return false;
        }
        if (host.Length == 0 || host.Contains('@')) return false;
        if (port is not null)
        {
            if (!int.TryParse(port, out var p) || p is < 1 or > 65535) return false;
        }
        return true;
    }

    /// <summary>RFC 3986 userinfo 허용문자(unreserved/sub-delims/`:`)는 통과, `%XX` 보존, 나머지 인코딩.</summary>
    private static string EncodeUserinfo(string userinfo)
    {
        var sb = new StringBuilder(userinfo.Length);
        for (var i = 0; i < userinfo.Length; i++)
        {
            var ch = userinfo[i];
            if (ch == '%' && i + 2 < userinfo.Length && IsHexDigit(userinfo[i + 1]) && IsHexDigit(userinfo[i + 2]))
            {
                sb.Append(userinfo, i, 3);
                i += 2;
            }
            else if (IsUserinfoChar(ch))
            {
                sb.Append(ch);
            }
            else
            {
                var len = char.IsHighSurrogate(ch) && i + 1 < userinfo.Length && char.IsLowSurrogate(userinfo[i + 1]) ? 2 : 1;
                foreach (var b in Encoding.UTF8.GetBytes(userinfo.Substring(i, len)))
                    sb.Append('%').Append(b.ToString("X2"));
                i += len - 1;
            }
        }
        return sb.ToString();
    }

    private static bool IsHexDigit(char c) =>
        c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

    private static bool IsUserinfoChar(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
          or '-' or '.' or '_' or '~'                                   // unreserved
          or '!' or '$' or '&' or '\'' or '(' or ')' or '*' or '+' or ',' or ';' or '=' // sub-delims
          or ':';                                                       // user:password 구분

    /// <summary>
    /// MediaMTX 경로명/URL path 로 안전한 <b>ASCII</b> 문자만 남긴다(영숫자/`_`/`-`). MediaMTX 는 비-ASCII
    /// 경로명을 거부하므로(한글 카메라명이 조용히 등록 실패하던 원인) 반드시 ASCII 로 환원한다.
    /// 남는 게 없으면(예: 순수 한글명) 빈 문자열 — 호출부(<see cref="AssignSlugs"/>)가 "cam" 폴백/중복회피를 책임진다.
    /// 주의: <see cref="char.IsLetterOrDigit(char)"/> 는 유니코드 기준이라 한글도 letter 로 통과시키므로 쓰지 않는다.
    /// </summary>
    public static string ToAsciiSlug(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.Trim())
        {
            if (ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-')
                sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 카메라 목록에서 <see cref="CctvCamera.Slug"/>가 비어 있는 항목에만 "cam1", "cam2", … 형태의 안정 경로명을 부여한다(in-place).
    /// 이미 slug 가 있는 카메라는 건드리지 않아 표시명/순서 변경에도 경로가 안정 유지된다(MediaMTX 재등록·오버레이 흔들림 방지).
    /// </summary>
    public static void AssignSlugs(List<CctvCamera> cameras)
    {
        if (cameras is null) return;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 1차: 이미 할당된 slug 를 예약
        foreach (var cam in cameras)
            if (!string.IsNullOrWhiteSpace(cam.Slug))
                used.Add(cam.Slug);
        // 2차: 빈 slug 에 cam1, cam2, … 부여
        var n = 1;
        foreach (var cam in cameras)
        {
            if (!string.IsNullOrWhiteSpace(cam.Slug)) continue;
            string slug;
            do { slug = $"cam{n++}"; } while (used.Contains(slug));
            used.Add(slug);
            cam.Slug = slug;
        }
    }

    public override void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
        base.Dispose();
    }

    private sealed class PathConfig
    {
        public string Source { get; set; } = "";
        public bool SourceOnDemand { get; set; }
    }
}
