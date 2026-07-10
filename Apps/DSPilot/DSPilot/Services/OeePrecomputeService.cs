// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Collections.Concurrent;
using DSPilot.Hubs;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR;

namespace DSPilot.Services;

/// <summary>
/// OEE 데이터 변경 신호 허브 — 쓰기 지점(레포/라이프사이클)이 정적 호출로 알리고,
/// <see cref="OeePrecomputeService"/> 가 구독해 표준 창 사전계산을 갱신한다.
/// 정적인 이유: 쓰기 지점이 레포 3곳+라이프사이클에 흩어져 있어 생성자 주입 리플을 피한다
/// (구독자는 사전계산 서비스 하나뿐이고, 놓친 신호는 주기 스윕이 상한을 보장하므로 느슨해도 안전).
/// </summary>
public static class OeeChangeSignal
{
    public static event Action<string?>? Changed;
    public static event Action? Invalidated;
    /// <summary>flow=null 은 라인 전체 영향(재구축/설정성 변경). 실패 무해 — 주기 스윕이 안전망.</summary>
    public static void Notify(string? flow = null)
    {
        try { Changed?.Invoke(flow); } catch { /* 구독자 예외가 쓰기 경로를 오염시키지 않게 */ }
    }

    /// <summary>
    /// 편집성/파괴성 변경(정지 분류·품질·표준CT·비생산 창·설정 저장·DB 초기화) — 사전계산 저장본을
    /// **즉시(동기) 폐기**해 직후의 재조회가 변경 전 JSON 을 받지 않게 한다. 폐기 동안 표준 창 요청은
    /// 라이브 계산으로 통과(정확)하고, 러너가 전 창을 수 초 내 재적재한다.
    /// 사이클성 삽입은 Notify(오늘 창 이벤트 갱신)만 — 저장본 폐기를 남발하지 않는다.
    /// </summary>
    public static void NotifyInvalidate()
    {
        try { Invalidated?.Invoke(); } catch { /* 동일 — 호출 경로 보호 */ }
    }
}

public sealed class OeePrecomputeOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>'오늘' 창 전 스코프 스윕 주기(초) — push 실시간성의 상한이자 stale 상한.</summary>
    public int TodaySweepSeconds { get; set; } = 20;
    public int WeekSweepSeconds { get; set; } = 60;
    public int MonthSweepSeconds { get; set; } = 300;
    public int YesterdaySweepSeconds { get; set; } = 600;
    /// <summary>변경 이벤트 디바운스(초) — 사이클 폭주 시 재계산 밀도 상한은 EventMinIntervalSeconds.</summary>
    public int EventDebounceSeconds { get; set; } = 2;
    public int EventMinIntervalSeconds { get; set; } = 5;
}

/// <summary>
/// P2 사전계산+push — 표준 창(오늘/7일/30일/60일/어제)의 OEE 엔드포인트 응답 JSON 을 백그라운드에서
/// 완성 상태로 유지하고, 미들웨어(Program.cs)가 일치하는 GET 요청을 저장본으로 즉시 응답(O(1))한다.
/// 갱신 시 SignalR "OeePrecomputed" 를 push — 프런트는 폴링(60초 안전망) 대신 push 로 재조회한다.
///
/// 계산 방식 = **셀프 HTTP 호출**(X-Dsp-Fresh 헤더로 미들웨어 단락 우회): 컨트롤러의 집계 로직을
/// 추출/복제하지 않고 실제 프로덕션 코드 경로를 그대로 실행하므로, 저장본과 온디맨드 계산의 결과가
/// 정의상 동일하다(시맨틱 드리프트 0). 하부의 TTL 캐시·인메모리 미러가 번들 내 중복 계산을 흡수한다.
/// stale 상한 = 각 창의 스윕 주기(놓친 무효화 신호가 있어도 이 주기 안에 회복 — 기존 폴링 지연과 동급).
/// </summary>
public sealed class OeePrecomputeService : IHostedService, IDisposable
{
    /// <summary>표준 창 이름. 미들웨어 매칭과 저장 키에 사용.</summary>
    private static readonly string[] AllWindows = ["today", "7d", "30d", "60d", "yesterday"];

    // (경로, 대상 창, flow 스코프 지원 여부). summary 를 각 번들의 맨 앞에 — 공유 캐시(임계/집계) 워밍.
    private static readonly (string Path, string[] Windows, bool PerFlow)[] Registry =
    {
        ("/api/oee/summary", ["today", "7d", "30d", "60d", "yesterday"], true),
        ("/api/oee/daily", ["today", "7d", "30d", "60d"], true),
        ("/api/oee/downtime", ["today", "7d", "30d", "60d"], true),
        ("/api/oee/plan-time", ["today", "7d", "30d", "60d"], true),
        ("/api/oee/planned-stops/actual", ["today", "7d", "30d", "60d"], true),
        ("/api/oee/ranking", ["today", "7d", "30d", "60d"], false),
        ("/api/oee/teep", ["today", "7d", "30d", "60d", "yesterday"], true),
        ("/api/oee/teep/matrix", ["today", "7d", "30d", "60d"], false),
        ("/api/oee/output-count", ["today", "yesterday"], false),
    };

    private sealed record Entry(byte[] Json, DateTime ComputedUtc);

    private readonly OeePrecomputeOptions _opt;
    private readonly IServer _server;
    private readonly DspDbService _db;
    private readonly IHubContext<MonitoringHub> _hub;
    private readonly ILogger<OeePrecomputeService> _logger;

    private readonly ConcurrentDictionary<string, Entry> _store = new(StringComparer.Ordinal);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private CancellationTokenSource? _cts;
    private Task? _runner;
    private string? _baseUrl;
    private volatile bool _dirty;
    private DateTime _dirtyAtUtc;
    private DateTime _lastEventRefreshUtc = DateTime.MinValue;
    private readonly Dictionary<string, DateTime> _lastSweepUtc = new();

    // 무효화(편집/자정) 세대 — 무효화와 교차한 재계산 결과가 클리어 뒤에 저장되는 것을 막는다.
    // _epoch 증가+클리어와 가드+저장을 같은 락으로 묶어 TOCTOU 를 봉쇄(경합 빈도는 러너 초당 수 회 수준).
    private readonly object _storeSync = new();
    private int _epoch;
    private volatile bool _sweepResetRequested; // _lastSweepUtc 는 러너 스레드 소유 — 리셋은 플래그로 위임

    public OeePrecomputeService(
        OeePrecomputeOptions opt, IServer server, DspDbService db,
        IHubContext<MonitoringHub> hub, ILogger<OeePrecomputeService> logger)
    {
        _opt = opt;
        _server = server;
        _db = db;
        _hub = hub;
        _logger = logger;
    }

    // ── 미들웨어 서빙 ────────────────────────────────────────────────────────

    /// <summary>
    /// 요청이 표준 창과 일치하고 신선한 저장본이 있으면 반환. 쿼리에 from/to/flow 외 키가 있거나
    /// (필터 조회) 창 분류 실패/저장본 stale 이면 null — 호출측은 기존 라이브 계산으로 통과시킨다.
    /// </summary>
    public byte[]? TryServe(HttpRequest req, out long ageMs)
    {
        ageMs = 0;
        if (!_opt.Enabled) return null;
        if (req.Headers.ContainsKey("X-Dsp-Fresh")) return null; // 사전계산 자신의 셀프 호출

        var path = req.Path.Value ?? "";
        var spec = Registry.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        if (spec.Path is null) return null;

        string? flow = null;
        DateTime? from = null, to = null;
        foreach (var kv in req.Query)
        {
            switch (kv.Key.ToLowerInvariant())
            {
                case "from": if (DateTime.TryParse(kv.Value, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var f)) from = f; else return null; break;
                case "to": if (DateTime.TryParse(kv.Value, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var t)) to = t; else return null; break;
                case "flow": flow = string.IsNullOrWhiteSpace(kv.Value) ? null : kv.Value.ToString(); break;
                default: return null; // status/reason/detected 등 필터 조회는 라이브 경로로
            }
        }
        if (from is null || to is null) return null;
        if (flow is not null && !spec.PerFlow) return null;

        var window = ClassifyWindow(from.Value, to.Value);
        if (window is null || !spec.Windows.Contains(window)) return null;

        if (!_store.TryGetValue(Key(spec.Path, window, flow), out var entry)) return null;

        var age = DateTime.UtcNow - entry.ComputedUtc;
        if (age > TimeSpan.FromSeconds(SweepSeconds(window) * 3 + 30)) return null; // stale — 라이브 폴백

        ageMs = (long)age.TotalMilliseconds;
        return entry.Json;
    }

    /// <summary>
    /// from/to(로컬)를 표준 창으로 분류. 프리셋 정의(uptime rangeForPeriod / dashboard loadCompare)와 동일:
    /// today/Nd 는 from=오늘 00:00(−N+1일), to≈지금. yesterday 는 어제 00:00 ~ 오늘 00:00.
    /// </summary>
    private static string? ClassifyWindow(DateTime fromLocal, DateTime toLocal)
    {
        var todayStart = DateTime.Now.Date;
        var now = DateTime.Now;
        bool Near(DateTime a, DateTime b, int seconds) => Math.Abs((a - b).TotalSeconds) <= seconds;
        bool ToIsNow() => toLocal >= now.AddSeconds(-180) && toLocal <= now.AddSeconds(30);

        if (Near(fromLocal, todayStart, 1) && ToIsNow()) return "today";
        if (Near(fromLocal, todayStart.AddDays(-6), 1) && ToIsNow()) return "7d";
        if (Near(fromLocal, todayStart.AddDays(-29), 1) && ToIsNow()) return "30d";
        if (Near(fromLocal, todayStart.AddDays(-59), 1) && ToIsNow()) return "60d";
        if (Near(fromLocal, todayStart.AddDays(-1), 1) && Near(toLocal, todayStart, 120)) return "yesterday";
        return null;
    }

    private static string Key(string path, string window, string? flow) => $"{path}|{window}|{flow ?? ""}";

    private static int SweepSecondsStatic(OeePrecomputeOptions o, string window) => window switch
    {
        "today" => o.TodaySweepSeconds,
        "7d" => o.WeekSweepSeconds,
        "yesterday" => o.YesterdaySweepSeconds,
        _ => o.MonthSweepSeconds,
    };
    private int SweepSeconds(string window) => SweepSecondsStatic(_opt, window);

    // ── 수명 주기 ───────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_opt.Enabled)
        {
            _logger.LogInformation("[Precompute] Enabled=false — 전 경로 라이브 계산으로 동작");
            return Task.CompletedTask;
        }
        OeeChangeSignal.Changed += OnChanged;
        OeeChangeSignal.Invalidated += OnInvalidated;
        _cts = new CancellationTokenSource();
        _runner = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        OeeChangeSignal.Changed -= OnChanged;
        OeeChangeSignal.Invalidated -= OnInvalidated;
        _cts?.Cancel();
        if (_runner is not null)
        {
            try { await _runner.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
            catch { /* shutdown */ }
        }
    }

    public void Dispose() => _cts?.Dispose();

    private void OnChanged(string? flow)
    {
        // 스코프 정밀 추적 대신 단순 dirty 플래그 — 이벤트 갱신은 '오늘' 창 전 스코프를 다시 계산한다
        // (사이클 1건도 라인 KPI 에 영향, flow 구분 이득이 작고 하부 캐시·미러가 단가를 이미 고정).
        _dirty = true;
        _dirtyAtUtc = DateTime.UtcNow;
    }

    private void OnInvalidated()
    {
        // 호출 스레드(편집 API 응답 직전)에서 동기 클리어 — 편집 직후 프런트의 재조회가 변경 전
        // 저장본을 받지 않게 한다. 클리어 동안 표준 창 요청은 라이브 계산으로 통과하고,
        // 스윕 리셋으로 전 창이 초당 1개씩(≈5초 내) 재적재된다. 자정 롤오버도 같은 경로.
        lock (_storeSync)
        {
            _epoch++;
            _store.Clear();
        }
        _sweepResetRequested = true;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // Kestrel 기동 후에야 주소가 잡힌다(호스팅 서비스가 서버보다 먼저 시작) — 준비될 때까지 대기.
        while (!ct.IsCancellationRequested && _baseUrl is null)
        {
            _baseUrl = ResolveBaseUrl();
            if (_baseUrl is null)
            {
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }
            }
        }
        _logger.LogInformation("[Precompute] 시작 — base={Base}, today {T}s / 7d {W}s / 30·60d {M}s / yesterday {Y}s",
            _baseUrl, _opt.TodaySweepSeconds, _opt.WeekSweepSeconds, _opt.MonthSweepSeconds, _opt.YesterdaySweepSeconds);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var lastDateLocal = DateTime.Now.Date;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(ct);
                var nowUtc = DateTime.UtcNow;

                // 자정 경과 — 표준 창 정의(오늘 00:00 기준)가 통째로 이동하므로 저장본 전부 무효.
                // 방치하면 '어제' 창이 그저께 데이터를 다음 스윕까지(최대 10분) 서빙하는 등
                // 창 이름과 내용이 어긋난다.
                var todayLocal = DateTime.Now.Date;
                if (todayLocal != lastDateLocal)
                {
                    lastDateLocal = todayLocal;
                    OnInvalidated();
                    _logger.LogInformation("[Precompute] 날짜 변경 — 저장본 무효화 후 전 창 재계산");
                }

                // 무효화(편집/자정) 직후 — 전 창을 처음부터 다시 스윕(today 부터 초당 1개).
                if (_sweepResetRequested)
                {
                    _sweepResetRequested = false;
                    _lastSweepUtc.Clear();
                }

                // 변경 이벤트 → '오늘' 창 갱신 (디바운스 + 최소 간격)
                if (_dirty
                    && nowUtc - _dirtyAtUtc >= TimeSpan.FromSeconds(_opt.EventDebounceSeconds)
                    && nowUtc - _lastEventRefreshUtc >= TimeSpan.FromSeconds(_opt.EventMinIntervalSeconds))
                {
                    _dirty = false;
                    _lastEventRefreshUtc = nowUtc;
                    await RefreshWindowAsync("today", ct);
                    _lastSweepUtc["today"] = nowUtc;
                    continue;
                }

                foreach (var window in AllWindows)
                {
                    var last = _lastSweepUtc.GetValueOrDefault(window, DateTime.MinValue);
                    if (nowUtc - last >= TimeSpan.FromSeconds(SweepSeconds(window)))
                    {
                        _lastSweepUtc[window] = nowUtc;
                        await RefreshWindowAsync(window, ct);
                        break; // 틱당 창 1개만 — 부하 평탄화
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Precompute] 갱신 루프 오류");
            }
        }
    }

    private string? ResolveBaseUrl()
    {
        try
        {
            var addr = _server.Features.Get<IServerAddressesFeature>()?.Addresses?.FirstOrDefault(a => a.StartsWith("http:"));
            if (string.IsNullOrEmpty(addr)) return null;
            var port = new Uri(addr.Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1").Replace("+", "127.0.0.1")).Port;
            return $"http://127.0.0.1:{port}";
        }
        catch { return null; }
    }

    /// <summary>창 1개의 전 스코프(라인+flow별) × 전 엔드포인트를 셀프 호출로 재계산해 저장하고 push.</summary>
    private async Task RefreshWindowAsync(string window, CancellationToken ct)
    {
        var (fromStr, toStr) = WindowRange(window);
        var flows = _db.Snapshot.Flows.Select(f => f.FlowName).Where(n => !string.IsNullOrEmpty(n)).ToList();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int ok = 0, fail = 0, drop = 0;

        foreach (var scope in new string?[] { null }.Concat(flows.Select(f => (string?)f)))
        {
            foreach (var (path, windows, perFlow) in Registry)
            {
                if (!windows.Contains(window)) continue;
                if (scope is not null && !perFlow) continue;
                if (ct.IsCancellationRequested) return;

                var url = $"{_baseUrl}{path}?from={Uri.EscapeDataString(fromStr)}&to={Uri.EscapeDataString(toStr)}"
                          + (scope is null ? "" : $"&flow={Uri.EscapeDataString(scope)}");
                try
                {
                    var epoch = _epoch; // 요청 전 세대 캡처 — 응답 대기 중 무효화가 끼면 결과 폐기
                    using var msg = new HttpRequestMessage(HttpMethod.Get, url);
                    msg.Headers.Add("X-Dsp-Fresh", "1");
                    using var res = await Http.SendAsync(msg, ct);
                    if (res.IsSuccessStatusCode)
                    {
                        var bytes = await res.Content.ReadAsByteArrayAsync(ct);
                        lock (_storeSync)
                        {
                            // 변경 전 상태로 계산된 결과일 수 있다 — 버리면 스윕 리셋이 곧 다시 채운다.
                            if (_epoch == epoch)
                            {
                                _store[Key(path, window, scope)] = new Entry(bytes, DateTime.UtcNow);
                                ok++;
                            }
                            else drop++;
                        }
                    }
                    else fail++;
                }
                catch (OperationCanceledException) { return; }
                catch { fail++; }
            }
        }

        _logger.LogDebug("[Precompute] {Window} 갱신 — {Ok}건 ({Ms}ms{Fail}{Drop})",
            window, ok, sw.ElapsedMilliseconds,
            fail > 0 ? $", 실패 {fail}" : "", drop > 0 ? $", 폐기 {drop}" : "");

        // push — 프런트(uptime 등)는 자기 창이면 재조회(저장본이라 ~2ms). 페이로드는 신호용 최소.
        try { await _hub.Clients.All.SendAsync("OeePrecomputed", new { window }, ct); }
        catch { /* 연결 없음 등 — 안전망 폴링이 커버 */ }
    }

    /// <summary>창 → (from,to) 로컬 ISO 문자열. 프런트 rangeForPeriod / loadCompare 와 동일 정의.</summary>
    private static (string From, string To) WindowRange(string window)
    {
        var now = DateTime.Now;
        var todayStart = now.Date;
        static string Iso(DateTime d) => d.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        return window switch
        {
            "today" => (Iso(todayStart), Iso(now)),
            "7d" => (Iso(todayStart.AddDays(-6)), Iso(now)),
            "30d" => (Iso(todayStart.AddDays(-29)), Iso(now)),
            "60d" => (Iso(todayStart.AddDays(-59)), Iso(now)),
            "yesterday" => (Iso(todayStart.AddDays(-1)), Iso(todayStart)),
            _ => (Iso(todayStart), Iso(now)),
        };
    }
}
