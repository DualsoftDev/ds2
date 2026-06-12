// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Collections.Concurrent;
using Ds2.Backend.Common;

namespace DSPilot.Services;

/// <summary>
/// Promaker SignalHub 가 보내는 <c>OnPlcConnectionStatus</c> 이벤트를 어댑터(Name) 단위로 캐시하고
/// UI 가 구독할 수 있는 이벤트로 노출. <see cref="HubSubscriberService"/> 가 hub event 를 받아 forward.
///
/// Promaker WPF 에서 PLAY 시 PLC 설정 오류(IP mismatch / 잘못된 vendor 등) 가 발생하면
/// Promaker.Agent 호스트의 PlcGateway 가 connect 실패를 감지하고 본 채널로 broadcast 한다.
/// DSPilot 은 캐시된 마지막 상태로 사용자에게 "PLC 통신 실패" 배너를 즉시 표시한다.
/// </summary>
public sealed class PlcConnectionStatusTracker
{
    private readonly ILogger<PlcConnectionStatusTracker> _logger;
    private readonly ConcurrentDictionary<string, PlcConnectionStatus> _statuses
        = new(StringComparer.OrdinalIgnoreCase);

    public PlcConnectionStatusTracker(ILogger<PlcConnectionStatusTracker> logger)
    {
        _logger = logger;
    }

    /// <summary>현재 캐시된 모든 PLC 어댑터 상태. UI 가 배너/대시보드에 렌더링.</summary>
    public IReadOnlyCollection<PlcConnectionStatus> CurrentStatuses =>
        _statuses.Values.ToList();

    /// <summary>최소 1개 PLC 가 disconnected 상태인지 — 배너 visibility 의 1차 게이트.</summary>
    public bool HasAnyDisconnected =>
        _statuses.Values.Any(s => !s.IsConnected);

    /// <summary>한 어댑터의 상태가 갱신될 때마다 발화. UI 가 StateHasChanged 호출용으로 구독.</summary>
    public event Action? StatusChanged;

    /// <summary>connected→disconnected *전이*에서만 1회 발화(지속 실패의 재시도 status 는 발화 안 함).
    /// 통신 blackout 처리용 — HubSubscriberService 가 구독해 진행 중 래치 사이클을 abandon 시킨다.</summary>
    public event Action<PlcConnectionStatus>? AdapterDisconnected;

    /// <summary>Hub 가 끊기면 모든 상태를 폐기 — 끊긴 동안 보여줄 stale 상태가 없도록.
    /// Hub 가 재연결되면 SignalHub.OnConnectedAsync 가 다시 스냅샷을 push 해 채워준다.</summary>
    public void ClearAll()
    {
        if (_statuses.IsEmpty) return;
        _statuses.Clear();
        RaiseChanged();
    }

    /// <summary>HubSubscriberService 가 OnPlcConnectionStatus 신호를 받았을 때 호출.</summary>
    public void Apply(PlcConnectionStatus status)
    {
        if (string.IsNullOrWhiteSpace(status.Name))
        {
            _logger.LogDebug("[Plc] Ignored PlcConnectionStatus with empty Name");
            return;
        }

        var previous = _statuses.TryGetValue(status.Name, out var p) ? p : default;
        _statuses[status.Name] = status;

        // 전이/사유 변화 시에만 Info, 동일 상태는 Trace — UI/구독자만 갱신 알림.
        if (previous is null
            || previous.IsConnected != status.IsConnected
            || previous.LastError != status.LastError)
        {
            if (status.IsConnected)
                _logger.LogInformation(
                    "[Plc] {Name} ({Vendor} {Ip}:{Port}) connected",
                    status.Name, status.Vendor, status.IpAddress, status.Port);
            else
                _logger.LogWarning(
                    "[Plc] {Name} ({Vendor} {Ip}:{Port}) disconnected — {Err}",
                    status.Name, status.Vendor, status.IpAddress, status.Port, status.LastError);
        }

        // 단절 *전이* 1회만 — 첫 status 가 이미 disconnected 인 경우(부팅 직후)도 포함.
        // (그 시점엔 진행 사이클이 없어 abandon 은 사실상 no-op — 안전.)
        if (!status.IsConnected && (previous is null || previous.IsConnected))
        {
            try { AdapterDisconnected?.Invoke(status); }
            catch (Exception ex) { _logger.LogDebug(ex, "[Plc] AdapterDisconnected subscriber threw"); }
        }

        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try { StatusChanged?.Invoke(); }
        catch (Exception ex) { _logger.LogDebug(ex, "[Plc] StatusChanged subscriber threw"); }
    }
}
