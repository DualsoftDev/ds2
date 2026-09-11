using System;
using System.Collections.Generic;
using System.Linq;
using Ds2.Core.Store;
using AidMicrexSxConnectionInfo = Ds2.Core.StandardSubmodels.AssetInterfacesDescriptionTypes.AidMicrexSxConnectionInfo;
using AidMicrexSxEndpointSettings = Ds2.Core.StandardSubmodels.AssetInterfacesDescriptionTypes.AidMicrexSxEndpointSettings;
using AidXgtEndpointSettings = Ds2.Core.StandardSubmodels.AssetInterfacesDescriptionTypes.AidXgtEndpointSettings;
using AssetInterfacesDescription = Ds2.Core.StandardSubmodels.AssetInterfacesDescriptionTypes.AssetInterfacesDescription;

namespace Promaker.Shared;

/// <summary>
/// Promaker PLC 설정 ↔ AID InterfaceMicrexSx endpoint 동기화 경계.
/// <see cref="AidXgtEndpointSynchronizer"/> 의 형제이며, 접속정보의 정본은 양쪽 모두 AID 다.
///
/// AID 에 실어야 하는 이유: Promaker.Agent 는 게이트웨이를 <b>AID 에서만</b> 조립한다
/// (MonitoringSupervisor → AidXgtGatewayConfig.buildForProject). 전역 PlcConnection.json 은
/// 설정 지문·스캔주기 용도로만 읽으므로, 거기에만 저장하면 인앱 실행은 되고 런타임
/// 모니터링·DSPilot 은 PLC 에 닿지 못한다.
/// </summary>
public static class AidMicrexSxEndpointSynchronizer
{
    /// <summary>지정 System 의 SX endpoint 를 읽는다. 없으면 null.</summary>
    public static AidMicrexSxConnectionInfo? TryReadFromStore(DsStore? store, Guid systemId)
    {
        var project = AidXgtEndpointSynchronizer.FindOwningProject(store, systemId);
        var aidOption = project?.AssetInterfaces;
        if (aidOption is null
            || !Microsoft.FSharp.Core.FSharpOption<AssetInterfacesDescription>.get_IsSome(aidOption))
            return null;

        return AidMicrexSxEndpointSettings.TryReadForSystem(aidOption.Value, systemId);
    }

    /// <summary>
    /// 지정 System 의 SX 바인딩을 보장한다 — 없으면 <paramref name="addresses"/> 로 만들고,
    /// 있으면 endpoint 를 갱신하고 새 주소만 병합한다. AID 자체가 없으면 만든다.
    ///
    /// 같은 System 의 XGT 바인딩은 <b>함께 지운다</b>. 남겨 두면 Agent 가 AID 에서 연결을
    /// 하나 더 만들어 "1대만 설정했는데 2대" 가 된다 — 실제로 현장에서 그렇게 나타났다.
    /// </summary>
    public static bool EnsureToStore(
        DsStore? store,
        Guid systemId,
        PlcConnectionSettings? settings,
        IEnumerable<string>? addresses)
    {
        if (store is null || settings is null || !settings.WasPersisted)
            return false;

        var project = AidXgtEndpointSynchronizer.FindOwningProject(store, systemId);
        if (project is null)
            return false;

        AssetInterfacesDescription aid;
        var aidOption = project.AssetInterfaces;
        if (aidOption is not null
            && Microsoft.FSharp.Core.FSharpOption<AssetInterfacesDescription>.get_IsSome(aidOption))
        {
            aid = aidOption.Value;
        }
        else
        {
            aid = new AssetInterfacesDescription();
            project.AssetInterfaces =
                Microsoft.FSharp.Core.FSharpOption<AssetInterfacesDescription>.Some(aid);
        }

        var written = AidMicrexSxEndpointSettings.EnsureBindingForSystem(
            aid,
            systemId,
            (settings.IpAddress ?? "").Trim(),
            settings.Port,
            settings.SxIoMapPath ?? string.Empty,
            settings.SxWritableAreas ?? new List<string>(),
            settings.TimeoutMs,
            settings.ScanIntervalMs,
            addresses ?? Array.Empty<string>());

        if (written <= 0) return false;

        AidXgtEndpointSettings.RemoveForSystem(aid, systemId);
        return true;
    }

    /// <summary>이 System 의 SX 바인딩을 지운다 — 벤더를 SX 에서 LS 로 되돌릴 때 호출한다.</summary>
    public static int RemoveFromStore(DsStore? store, Guid systemId)
    {
        var project = AidXgtEndpointSynchronizer.FindOwningProject(store, systemId);
        var aidOption = project?.AssetInterfaces;
        if (aidOption is null
            || !Microsoft.FSharp.Core.FSharpOption<AssetInterfacesDescription>.get_IsSome(aidOption))
            return 0;

        return AidMicrexSxEndpointSettings.RemoveForSystem(aidOption.Value, systemId);
    }
}
