// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using Ds2.Backend.Common;
using Ds2.Core.StandardSubmodels;

namespace DSPilot.Services;

/// <summary>
/// Agent/Edge 가 보고한 <see cref="PlcConnectionStatus"/> 의 접속 표기 — 폴백을 한 곳에.
///
/// 정본은 게이트웨이가 채운 <c>Endpoint</c>(Ds2.Core PlcEndpointLabel: host:port | USB | USB(selector))다.
/// 그 필드가 없는 구버전 Agent 보고는 ip:port 로 조립하고, 그것도 없으면 "" 를 돌려준다 — 호출자는 빈
/// 표기를 "모름"으로 다루고 모델 매칭 칩을 그리지 않는다. 빈 문자열을 모델 endpoint 와 대조하면 전부
/// '미매칭'이 되어 정상 현장 전체가 경고색으로 물든다.
/// </summary>
public static class PlcEndpointDisplay
{
    private static readonly string TcpLabel =
        AssetInterfacesDescriptionTypes.XgtEndpointBase.transportLabel(AssetInterfacesDescriptionTypes.XgtTransport.XgtTcp);

    public static string Of(PlcConnectionStatus s) => Of(s.Endpoint, s.IpAddress, s.Port);

    public static string Of(string? endpoint, string? ip, int port)
    {
        if (!string.IsNullOrWhiteSpace(endpoint)) return endpoint.Trim();
        if (!string.IsNullOrWhiteSpace(ip) && port > 0)
            return PlcEndpointLabel.format(TcpLabel, ip.Trim(), port, string.Empty);
        return string.Empty;
    }

    /// <summary>어댑터 보고의 USB 판정 — Hub 계약이 USB 접속은 IpAddress ""·Port 0 으로 보낸다.
    /// Endpoint 가 있는(=매체 축을 아는 신 Agent) 보고에서만 의미가 있다.</summary>
    public static bool IsUsb(PlcConnectionStatus s) =>
        !string.IsNullOrWhiteSpace(s.Endpoint) && string.IsNullOrWhiteSpace(s.IpAddress) && s.Port <= 0;

    /// <summary>모델(AID) endpoint 의 USB 판정 — <see cref="PlcEndpointInfo"/> 도 USB 는 Ip ""·Port 0.</summary>
    public static bool IsUsb(PlcEndpointInfo e) => string.IsNullOrWhiteSpace(e.Ip) && e.Port <= 0;
}
