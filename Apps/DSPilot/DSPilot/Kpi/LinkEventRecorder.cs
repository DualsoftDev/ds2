// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Collections.Concurrent;
using Ds2.Backend.Common;
using DSPilot.Services;

namespace DSPilot.Kpi;

/// <summary>
/// 시스템(PLC 연결)별 접속 전이와 수집 심박 공백을 기록한다. doc/30 §7.
/// <para>
/// ★ 이 데이터는 <b>계산 인자가 아니다.</b> 연표 오버레이와 행 배지로만 쓴다. 통신이 끊긴 구간을
/// 가로지른 사이클은 길이 규칙대로 비생산이 되고, 사람이 배지를 보고 대조한다.
/// </para>
/// <para>
/// 두 소스를 합친다. 시스템별 상태는 Agent 가 보내는 <c>OnPlcConnectionStatus</c>(어댑터 Name 포함)에서,
/// 수집기 생존은 <c>OnScanHeartbeat</c>(1초 스로틀, payload 없음 → 라인 공통)에서 온다.
/// 1초 심박 자체는 저장하지 않는다 — 침묵이 임계를 넘을 때 공백 1행을 열고, 돌아오면 닫는다.
/// </para>
/// </summary>
public sealed class LinkEventRecorder : BackgroundService
{
    /// <summary>이 시간 이상 심박이 없으면 공백을 연다(심박 주기 1초의 여유 배수).</summary>
    public static readonly TimeSpan GapThreshold = TimeSpan.FromSeconds(10);

    /// <summary>공백 감시 주기.</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);

    /// <summary>라인 공통 심박의 시스템 키 — 어댑터별이 아님을 나타낸다.</summary>
    public const string LineSystem = "*";

    private readonly KpiRepository _repo;
    private readonly PlcConnectionStatusTracker _tracker;
    private readonly ILogger<LinkEventRecorder> _logger;

    private readonly ConcurrentDictionary<string, bool> _lastState = new(StringComparer.OrdinalIgnoreCase);
    private long _lastHeartbeatMs;
    private bool _gapOpen;
    private bool _everHeartbeat;

    public LinkEventRecorder(
        KpiRepository repo,
        PlcConnectionStatusTracker tracker,
        ILogger<LinkEventRecorder> logger)
    {
        _repo = repo;
        _tracker = tracker;
        _logger = logger;
    }

    /// <summary>Agent 의 1초 심박. 저장하지 않고 마지막 수신 시각만 갱신한다.</summary>
    public void OnHeartbeat()
    {
        Interlocked.Exchange(ref _lastHeartbeatMs, KpiTime.NowMs());
        _everHeartbeat = true;
    }

    /// <summary>어댑터 상태 수신 — 전이일 때만 1행 남긴다.</summary>
    public void OnPlcStatus(PlcConnectionStatus status)
    {
        if (string.IsNullOrWhiteSpace(status.Name)) return;

        bool had = _lastState.TryGetValue(status.Name, out var prev);
        if (had && prev == status.IsConnected) return;
        _lastState[status.Name] = status.IsConnected;

        var at = status.AtUtc == default ? KpiTime.NowMs() : KpiTime.ToMs(status.AtUtc);
        _ = RecordAsync(new LinkEventRecord(
            status.Name,
            at,
            null,
            status.IsConnected,
            LinkEventRecord.KindLink,
            status.IsConnected ? null : NullIfBlank(status.LastError),
            PlcEndpointDisplay.Of(status)));
    }

    /// <summary>Hub 단절 — 어댑터 상태를 더 이상 믿을 수 없으므로 캐시를 비운다.</summary>
    public void OnHubDisconnected() => _lastState.Clear();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _tracker.AdapterTransitioned += OnPlcStatus;

        // 기동 표시 — 이 행이 없는 구간은 DSPilot 자체가 꺼져 있던 시간이다.
        await RecordAsync(new LinkEventRecord(
            LineSystem, KpiTime.NowMs(), null, true, LinkEventRecord.KindBoot, null, null));

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(CheckInterval, stoppingToken);
                await CheckGapAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) { /* 정상 종료 */ }
        finally
        {
            _tracker.AdapterTransitioned -= OnPlcStatus;
        }
    }

    private async Task CheckGapAsync(CancellationToken ct)
    {
        // 심박을 한 번도 못 받은 상태(시뮬레이션·Agent 미연결)는 공백으로 주장하지 않는다.
        if (!_everHeartbeat) return;

        var now = KpiTime.NowMs();
        var silent = now - Interlocked.Read(ref _lastHeartbeatMs);

        if (!_gapOpen && silent > GapThreshold.TotalMilliseconds)
        {
            _gapOpen = true;
            await RecordAsync(new LinkEventRecord(
                LineSystem,
                Interlocked.Read(ref _lastHeartbeatMs),
                null,
                false,
                LinkEventRecord.KindGap,
                "scan heartbeat silent",
                null), ct);
            _logger.LogWarning("[Kpi] scan heartbeat gap opened — silent {Ms}ms", silent);
        }
        else if (_gapOpen && silent <= GapThreshold.TotalMilliseconds)
        {
            _gapOpen = false;
            await _repo.CloseOpenGapAsync(LineSystem, Interlocked.Read(ref _lastHeartbeatMs), ct);
            _logger.LogInformation("[Kpi] scan heartbeat resumed");
        }
    }

    private async Task RecordAsync(LinkEventRecord e, CancellationToken ct = default)
    {
        try { await _repo.InsertLinkEventAsync(e, ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "[Kpi] link event write failed — {System}", e.System); }
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
