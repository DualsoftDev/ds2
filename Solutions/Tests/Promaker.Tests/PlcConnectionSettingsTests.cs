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

    /// <summary>
    /// 킬 스위치가 ViewModel ↔ POCO 왕복에서 살아남는지. 배선이 빠지면 두 가지가 동시에 깨진다:
    /// ① Promaker 가 항상 기본값 true 로 판단해 킬 스위치가 무시되고,
    /// ② 저장할 때마다 PlcConnection.json 의 값을 true 로 되돌려 Agent 쪽 설정까지 무력화한다.
    /// </summary>
    [Fact]
    public void PreferAasxPlcConnection_survives_viewmodel_poco_roundtrip()
    {
        var poco = new PromakerShared.PlcConnectionSettings { PreferAasxPlcConnection = false };
        poco.EnsureProfiles();

        var vm = PlcSettings.FromPoco(poco);
        Assert.False(vm.PreferAasxPlcConnection);

        var back = vm.ToPoco();
        Assert.False(back.PreferAasxPlcConnection);
    }

    /// <summary>파일에 false 로 저장된 값을 로드 → 저장했을 때 true 로 되돌아가지 않아야 한다.</summary>
    [Fact]
    public void Saving_does_not_reset_prefer_aasx_flag()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "Promaker.Tests", nameof(PlcConnectionSettingsTests), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "PlcConnection.json");

        try
        {
            Directory.CreateDirectory(root);
            var original = new PromakerShared.PlcConnectionSettings { PreferAasxPlcConnection = false };
            Assert.True(original.TrySave(path));

            var loaded = PromakerShared.PlcConnectionSettings.LoadOrDefault(path);
            Assert.False(loaded.PreferAasxPlcConnection);

            // ViewModel 을 거친 뒤 다시 저장 — 여기서 true 로 되돌아가면 킬 스위치가 무력화된다.
            var resaved = PlcSettings.FromPoco(loaded).ToPoco();
            Assert.True(resaved.TrySave(path));

            Assert.False(PromakerShared.PlcConnectionSettings.LoadOrDefault(path).PreferAasxPlcConnection);
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
    /// 프로젝트 접속 정보를 적용하면 활성 벤더 프로파일도 함께 갱신되어야 한다.
    /// 이 경로는 PlcConnection.json 을 쓰지 않으므로(Agent 재시작 방지) Save() 안의 프로파일 캡처를
    /// 거치지 않는다 — 갱신이 빠지면 PLC 다이얼로그에서 벤더를 토글했다 돌아올 때 프로젝트 값이
    /// 옛 로컬 값으로 되돌아간다.
    /// </summary>
    [Fact]
    public void ApplyConnection_updates_active_vendor_profile()
    {
        var vm = new PlcSettings();
        vm.VendorProfiles[nameof(PromakerShared.PlcVendorChoice.Mitsubishi)] =
            PromakerShared.PlcVendorProfile.Defaults(PromakerShared.PlcVendorChoice.Mitsubishi);

        vm.ApplyConnection(new PromakerShared.AasxPlcConnection(
            PromakerShared.PlcVendorChoice.Mitsubishi, "10.20.30.40", 5007,
            IsUdp: true, NetworkNumber: 3, StationNumber: 12, LocalEthernet: true, TimeoutMs: 7000,
            ProfileVersion: PromakerShared.PlcConnectionResolver.CurrentProfileVersion));

        var profile = vm.VendorProfiles[nameof(PromakerShared.PlcVendorChoice.Mitsubishi)];
        Assert.Equal("10.20.30.40", profile.IpAddress);
        Assert.Equal(5007, profile.Port);
        Assert.True(profile.IsUdp);
        Assert.Equal(12, profile.StationNumber);

        // 벤더를 떠났다 돌아와도 프로젝트 값이 복원되어야 한다.
        vm.ApplyProfile(PromakerShared.PlcVendorProfile.Defaults(PromakerShared.PlcVendorChoice.LsXgi));
        vm.ApplyProfile(vm.VendorProfiles[nameof(PromakerShared.PlcVendorChoice.Mitsubishi)]);
        Assert.Equal("10.20.30.40", vm.IpAddress);
        Assert.True(vm.IsUdp);
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
