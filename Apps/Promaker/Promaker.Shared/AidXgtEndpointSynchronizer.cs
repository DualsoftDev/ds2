using System;
using System.Linq;
using Ds2.Core;
using Ds2.Core.StandardSubmodels;
using Ds2.Core.Store;
using AidXgtConnectionInfo = Ds2.Core.StandardSubmodels.AssetInterfacesDescriptionTypes.AidXgtConnectionInfo;
using AidXgtEndpointSettings = Ds2.Core.StandardSubmodels.AssetInterfacesDescriptionTypes.AidXgtEndpointSettings;
using AssetInterfacesDescription = Ds2.Core.StandardSubmodels.AssetInterfacesDescriptionTypes.AssetInterfacesDescription;

namespace Promaker.Shared;

/// <summary>
/// Promaker의 PLC 입력값과 AID InterfaceXGT endpoint를 동기화한다.
/// PLC 접속 및 수집 설정의 유일한 정본은 AID다.
/// </summary>
public static class AidXgtEndpointSynchronizer
{
    private static Project? FindOwningProject(DsStore? store, Guid systemId) =>
        store?.Projects.Values.FirstOrDefault(project => project.ActiveSystemIds.Contains(systemId));

    private static Guid? TryGetOnlyActiveSystemId(Project? project) =>
        project is not null && project.ActiveSystemIds.Count == 1
            ? project.ActiveSystemIds[0]
            : null;

    /// <summary>
    /// 단일 System 프로젝트의 AID InterfaceXGT endpoint를 읽는다.
    /// 다중 System 프로젝트는 잘못된 PLC 프로필을 임의로 고르지 않도록 null을 반환한다.
    /// </summary>
    public static AidXgtConnectionInfo? TryReadFromStore(DsStore? store)
    {
        var project = store?.Projects.Values.FirstOrDefault();
        var systemId = TryGetOnlyActiveSystemId(project);
        if (systemId is null)
            return null;
        return TryReadFromStore(store, systemId.Value);
    }

    /// <summary>지정한 active System에 연결된 AID InterfaceXGT endpoint를 읽는다.</summary>
    public static AidXgtConnectionInfo? TryReadFromStore(DsStore? store, Guid systemId)
    {
        var project = FindOwningProject(store, systemId);
        var aidOption = project?.AssetInterfaces;
        if (aidOption is null
            || !Microsoft.FSharp.Core.FSharpOption<AssetInterfacesDescription>.get_IsSome(aidOption))
            return null;

        return AidXgtEndpointSettings.TryReadForSystem(aidOption.Value, systemId);
    }

    /// <summary>
    /// 현재 Promaker PLC 입력값을 AID의 모든 InterfaceXGT endpoint에 반영한다.
    /// AID가 없거나 XGT interface가 없으면 새 저장 위치를 만들지 않고 false를 반환한다.
    /// </summary>
    public static bool StampToStore(DsStore? store, PlcConnectionSettings? settings)
    {
        if (store is null || settings is null || !settings.WasPersisted)
            return false;

        var project = store.Projects.Values.FirstOrDefault();
        var systemId = TryGetOnlyActiveSystemId(project);
        return systemId is not null && StampToStore(store, systemId.Value, settings);
    }

    /// <summary>현재 PLC 입력값을 지정 System의 InterfaceXGT endpoint에만 반영한다.</summary>
    public static bool StampToStore(DsStore? store, Guid systemId, PlcConnectionSettings? settings)
    {
        if (store is null || settings is null || !settings.WasPersisted)
            return false;

        var project = FindOwningProject(store, systemId);
        var aidOption = project?.AssetInterfaces;
        if (aidOption is null
            || !Microsoft.FSharp.Core.FSharpOption<AssetInterfacesDescription>.get_IsSome(aidOption))
            return false;

        return AidXgtEndpointSettings.UpdateForSystem(
            aidOption.Value,
            systemId,
            settings.Vendor,
            (settings.IpAddress ?? "").Trim(),
            settings.Port,
            settings.IsUdp,
            settings.LocalEthernet,
            settings.NetworkNumber,
            settings.StationNumber,
            settings.TimeoutMs,
            settings.ScanIntervalMs) > 0;
    }

    /// <summary>
    /// XGT 수집 바인딩을 보장한다 — 없으면 <paramref name="addresses"/>(모델 IO맵 OUT/IN + UserTag)로
    /// InteractionMetadata 를 만들어 새로 생성하고, 있으면 endpoint 갱신과 새 주소 병합을 함께 한다. AID 자체가 없으면 만든다.
    /// bcf9121b 가 "생성" 경로를 빠뜨려 XGT 바인딩 없는 모델이 PLC IP 를 넣어도 반영 안 되던 구멍을 메운다.
    /// </summary>
    public static bool EnsureToStore(DsStore? store, PlcConnectionSettings? settings, IEnumerable<string>? addresses)
    {
        if (store is null || settings is null || !settings.WasPersisted)
            return false;

        var project = store.Projects.Values.FirstOrDefault();
        var systemId = TryGetOnlyActiveSystemId(project);
        return systemId is not null && EnsureToStore(store, systemId.Value, settings, addresses);
    }

    /// <summary>
    /// 지정한 active System용 XGT endpoint를 보장한다. 다른 System의 endpoint나
    /// InteractionMetadata는 변경하지 않는다.
    /// </summary>
    public static bool EnsureToStore(
        DsStore? store,
        Guid systemId,
        PlcConnectionSettings? settings,
        IEnumerable<string>? addresses)
    {
        if (store is null || settings is null || !settings.WasPersisted)
            return false;

        var project = FindOwningProject(store, systemId);
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

        return AidXgtEndpointSettings.EnsureBindingForSystem(
            aid,
            systemId,
            settings.Vendor,
            (settings.IpAddress ?? "").Trim(),
            settings.Port,
            settings.IsUdp,
            settings.LocalEthernet,
            settings.NetworkNumber,
            settings.StationNumber,
            settings.TimeoutMs,
            settings.ScanIntervalMs,
            addresses ?? System.Array.Empty<string>()) > 0;
    }

    /// <summary>AID endpoint를 Promaker PLC 입력값에 적용한다.</summary>
    public static void ApplyToSettings(PlcConnectionSettings settings, AidXgtConnectionInfo connection)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(connection);

        if (!Enum.TryParse<PlcVendorChoice>(connection.Vendor, ignoreCase: true, out var vendor))
            throw new ArgumentException($"Unsupported AID XGT vendor '{connection.Vendor}'.", nameof(connection));

        if (!string.Equals(settings.Vendor, connection.Vendor, StringComparison.OrdinalIgnoreCase))
            settings.ApplyProfileToFlat(vendor);

        settings.Vendor = connection.Vendor;
        settings.IpAddress = connection.IpAddress;
        settings.Port = connection.Port;
        settings.IsUdp = connection.IsUdp;
        settings.NetworkNumber = connection.NetworkNumber;
        settings.StationNumber = connection.StationNumber;
        settings.LocalEthernet = connection.LocalEthernet;
        if (connection.TimeoutMs > 0) settings.TimeoutMs = connection.TimeoutMs;
        if (connection.ScanIntervalMs > 0) settings.ScanIntervalMs = connection.ScanIntervalMs;
        settings.EnsureProfiles();
    }

    /// <summary>Promaker PLC 입력값이 AID endpoint와 같은지 비교한다.</summary>
    public static bool Matches(PlcConnectionSettings settings, AidXgtConnectionInfo connection) =>
        string.Equals(settings.Vendor, connection.Vendor, StringComparison.OrdinalIgnoreCase)
        && string.Equals((settings.IpAddress ?? "").Trim(), connection.IpAddress, StringComparison.OrdinalIgnoreCase)
        && settings.Port == connection.Port
        && settings.IsUdp == connection.IsUdp
        && settings.NetworkNumber == connection.NetworkNumber
        && settings.StationNumber == connection.StationNumber
        && settings.LocalEthernet == connection.LocalEthernet
        && (connection.TimeoutMs <= 0 || settings.TimeoutMs == connection.TimeoutMs)
        && (connection.ScanIntervalMs <= 0 || settings.ScanIntervalMs == connection.ScanIntervalMs);
}
