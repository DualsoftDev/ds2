using System;
using System.Linq;
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
    /// <summary>첫 번째 AID InterfaceXGT endpoint를 읽는다.</summary>
    public static AidXgtConnectionInfo? TryReadFromStore(DsStore? store)
    {
        var project = store?.Projects.Values.FirstOrDefault();
        var aidOption = project?.AssetInterfaces;
        if (aidOption is null
            || !Microsoft.FSharp.Core.FSharpOption<AssetInterfacesDescription>.get_IsSome(aidOption))
            return null;

        return AidXgtEndpointSettings.TryReadFirst(aidOption.Value);
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
        var aidOption = project?.AssetInterfaces;
        if (aidOption is null
            || !Microsoft.FSharp.Core.FSharpOption<AssetInterfacesDescription>.get_IsSome(aidOption))
            return false;

        return AidXgtEndpointSettings.UpdateAll(
            aidOption.Value,
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
