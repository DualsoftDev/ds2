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
    private readonly ILogger<CctvMediaMtxService> _logger;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool LastSyncOk { get; private set; }
    public string LastSyncMessage { get; private set; } = "동기화 전";
    public DateTime? LastSyncUtc { get; private set; }

    public CctvMediaMtxService(AppSettingsService settings, ILogger<CctvMediaMtxService> logger)
    {
        _settings = settings;
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

            var desired = cctv.Cameras
                .Where(c => c.Enabled && !string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.RtspUrl))
                .GroupBy(c => SanitizeName(c.Name), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var existing = await GetConfiguredPathNamesAsync(apiBase, ct);

            // 추가/갱신
            foreach (var (name, cam) in desired)
            {
                var body = JsonSerializer.Serialize(
                    new PathConfig { Source = cam.RtspUrl, SourceOnDemand = true }, JsonOptions);
                var verb = existing.Contains(name) ? "patch" : "add";
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                var res = await _http.PostAsync($"{apiBase}/v3/config/paths/{verb}/{Uri.EscapeDataString(name)}", content, ct);
                if (!res.IsSuccessStatusCode)
                    _logger.LogWarning("[CCTV] MediaMTX 경로 {Verb} 실패 {Name}: {Status}", verb, name, res.StatusCode);
            }

            // 설정에 없는 경로 제거 (MediaMTX 는 CCTV 전용 가정)
            foreach (var name in existing.Where(n => !desired.ContainsKey(n)))
            {
                var res = await _http.DeleteAsync($"{apiBase}/v3/config/paths/delete/{Uri.EscapeDataString(name)}", ct);
                if (!res.IsSuccessStatusCode)
                    _logger.LogWarning("[CCTV] MediaMTX 경로 삭제 실패 {Name}: {Status}", name, res.StatusCode);
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
    /// 공인주소(WebRtcAdditionalHosts) 가 비어 있으면 <b>무동작</b> — LAN 전용 의도 보존(기존 동작 그대로).
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
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 공인주소 미설정 = LAN 전용 의도 → 전역 설정에 일절 손대지 않는다.
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

    /// <summary>MediaMTX 경로명/URL path 로 안전한 문자만 남긴다.</summary>
    public static string SanitizeName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch is '_' or '-')
                sb.Append(ch);
        }
        return sb.Length > 0 ? sb.ToString() : "cam";
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
