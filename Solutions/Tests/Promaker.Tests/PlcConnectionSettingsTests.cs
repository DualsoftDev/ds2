using System;
using System.IO;
using Promaker.ViewModels;
using PromakerShared = Promaker.Shared;
using Xunit;

namespace Promaker.Tests;

public sealed class PlcConnectionSettingsTests
{
    [Fact]
    public void Defaults_use_100ms_scan_interval()
    {
        var settings = new PromakerShared.PlcConnectionSettings();
        settings.EnsureProfiles();

        Assert.Equal(100, PromakerShared.PlcConnectionSettings.DefaultScanIntervalMs);
        Assert.Equal(100, settings.ScanIntervalMs);
        Assert.All(settings.Profiles.Values, profile => Assert.Equal(100, profile.ScanIntervalMs));
        Assert.Equal(100, new PlcSettings().ScanIntervalMs);
    }

    [Fact]
    public void LoadOrDefault_upgrades_previous_50ms_default_interval()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "Promaker.Tests",
            nameof(PlcConnectionSettingsTests),
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "PlcConnection.json");

        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(path, """
                {
                  "vendor": "LsXgi",
                  "name": "PLC#1",
                  "ipAddress": "192.168.0.10",
                  "port": 2004,
                  "timeoutMs": 3000,
                  "scanIntervalMs": 50,
                  "localEthernet": true,
                  "networkNumber": 0,
                  "stationNumber": 255,
                  "isUdp": false,
                  "profiles": {
                    "LsXgi": {
                      "name": "PLC#1",
                      "ipAddress": "192.168.0.10",
                      "port": 2004,
                      "timeoutMs": 3000,
                      "scanIntervalMs": 50,
                      "localEthernet": true,
                      "networkNumber": 0,
                      "stationNumber": 255,
                      "isUdp": false
                    },
                    "Mitsubishi": {
                      "name": "PLC#1",
                      "ipAddress": "192.168.0.10",
                      "port": 5007,
                      "timeoutMs": 3000,
                      "scanIntervalMs": 50,
                      "localEthernet": true,
                      "networkNumber": 0,
                      "stationNumber": 255,
                      "isUdp": false
                    }
                  }
                }
                """);

            var settings = PromakerShared.PlcConnectionSettings.LoadOrDefault(path);

            Assert.Equal(100, settings.ScanIntervalMs);
            Assert.All(settings.Profiles.Values, profile => Assert.Equal(100, profile.ScanIntervalMs));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>출처 표식이 ViewModel 을 거치며 사라지면, 설정을 확정한 PC 에서도 프로젝트에
    /// 접속 정보가 기록되지 않는다(StampToStore 가 조용히 건너뜀).</summary>
    [Fact]
    public void WasPersisted_survives_viewmodel_poco_roundtrip()
    {
        var loaded = new PromakerShared.PlcConnectionSettings { WasPersisted = true };
        Assert.True(PlcSettings.FromPoco(loaded).ToPoco().WasPersisted);

        var fresh = new PromakerShared.PlcConnectionSettings();   // 파일 없음
        Assert.False(PlcSettings.FromPoco(fresh).ToPoco().WasPersisted);
    }

    /// <summary>
    /// AID endpoint를 적용하면 활성 벤더 프로파일도 함께 갱신되어야 한다.
    /// </summary>
    [Fact]
    public void ApplyConnection_updates_active_vendor_profile()
    {
        var vm = new PlcSettings();
        vm.VendorProfiles[nameof(PromakerShared.PlcVendorChoice.LsXgb)] =
            PromakerShared.PlcVendorProfile.Defaults(PromakerShared.PlcVendorChoice.LsXgb);

        vm.ApplyConnection(new Ds2.Core.StandardSubmodels.AssetInterfacesDescriptionTypes.AidXgtConnectionInfo(
            "xgt+tcp://10.20.30.40:2004", "LsXgb", "10.20.30.40", 2004,
            false, true, 3, 12, 7000, 250));

        var profile = vm.VendorProfiles[nameof(PromakerShared.PlcVendorChoice.LsXgb)];
        Assert.Equal("10.20.30.40", profile.IpAddress);
        Assert.Equal(2004, profile.Port);
        Assert.False(profile.IsUdp);
        Assert.Equal(12, profile.StationNumber);

        // 벤더를 떠났다 돌아와도 프로젝트 값이 복원되어야 한다.
        vm.ApplyProfile(PromakerShared.PlcVendorProfile.Defaults(PromakerShared.PlcVendorChoice.LsXgi));
        vm.ApplyProfile(vm.VendorProfiles[nameof(PromakerShared.PlcVendorChoice.LsXgb)]);
        Assert.Equal("10.20.30.40", vm.IpAddress);
        Assert.False(vm.IsUdp);
    }

    [Fact]
    public void LoadOrDefault_preserves_user_custom_interval()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "Promaker.Tests",
            nameof(PlcConnectionSettingsTests),
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "PlcConnection.json");

        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(path, """{ "vendor": "LsXgi", "scanIntervalMs": 200 }""");

            var settings = PromakerShared.PlcConnectionSettings.LoadOrDefault(path);

            Assert.Equal(200, settings.ScanIntervalMs);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
