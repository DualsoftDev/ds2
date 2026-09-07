using System;
using System.Collections.Generic;
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

    /// <summary>Promaker.ViewModels.PlcVendorChoice 는 Promaker.Shared 쪽 enum 의 별칭이다.
    /// 한쪽에만 벤더를 추가하면 ApplyConnection 의 Enum.Parse 가 그 벤더 이름에서 던지고,
    /// FromPoco 의 TryParse 는 조용히 실패해 저장된 벤더가 LsXgi 로 바뀐다.</summary>
    [Fact]
    public void PlcVendorChoice_mirror_matches_shared_enum()
    {
        var mirror = Enum.GetNames<PlcVendorChoice>();
        var shared = Enum.GetNames<PromakerShared.PlcVendorChoice>();
        Assert.Equal(shared, mirror);

        foreach (var name in shared)
        {
            Assert.Equal(
                (int)Enum.Parse<PromakerShared.PlcVendorChoice>(name),
                (int)Enum.Parse<PlcVendorChoice>(name));
        }
    }

    /// <summary>SX 전용 두 값은 프로파일이 아니라 POCO 플랫 필드에 있어서 ApplyProfile 이 채우지
    /// 않는다. ToPoco/FromPoco 짝에서 빠지면 Promaker 가 저장할 때 조용히 지워지고, SX 는
    /// 매핑표 없는 읽기 전용으로만 뜬다.</summary>
    [Fact]
    public void Sx_settings_survive_viewmodel_poco_roundtrip()
    {
        var loaded = new PromakerShared.PlcConnectionSettings
        {
            Vendor = nameof(PromakerShared.PlcVendorChoice.MicrexSx),
            SxIoMapPath = @"C:\plc\io_map.json",
            SxWritableAreas = new List<string> { "M1" },
        };

        var vm = PlcSettings.FromPoco(loaded);
        Assert.Equal(PlcVendorChoice.MicrexSx, vm.Vendor);
        Assert.Equal(@"C:\plc\io_map.json", vm.SxIoMapPath);
        Assert.Equal(new[] { "M1" }, vm.SxWritableAreas);

        var saved = vm.ToPoco();
        Assert.Equal(@"C:\plc\io_map.json", saved.SxIoMapPath);
        Assert.Equal(new[] { "M1" }, saved.SxWritableAreas);
    }

    /// <summary>AID InterfaceXGT 로 표현 가능한 벤더는 LS 세 종류뿐이다 — Ds2.Core 의
    /// XgtCpuModel(Xgi|Xgk|Xgb) 이 닫힌 DU 이기 때문이다. 이 판정이 저장 경로와 System 속성
    /// 패널의 화면 진실을 갈라놓는다. 비-LS 를 AID 쪽으로 잘못 분류하면 저장한 SX 벤더가
    /// 옛 LS endpoint 값으로 되돌아간다(실제로 그렇게 됐다).
    ///
    /// 권위 쪽(F#)이 정말 LS 만 받는지는 Ds2.Aasx.Tests 의
    /// "AID XGT endpoint accepts only LS vendors" 가 지킨다.</summary>
    [Fact]
    public void IsAidXgtVendor_is_exactly_the_LS_family()
    {
        Assert.True(PromakerShared.PlcVendorProfile.IsAidXgtVendor(PromakerShared.PlcVendorChoice.LsXgi));
        Assert.True(PromakerShared.PlcVendorProfile.IsAidXgtVendor(PromakerShared.PlcVendorChoice.LsXgk));
        Assert.True(PromakerShared.PlcVendorProfile.IsAidXgtVendor(PromakerShared.PlcVendorChoice.LsXgb));
        Assert.False(PromakerShared.PlcVendorProfile.IsAidXgtVendor(PromakerShared.PlcVendorChoice.Mitsubishi));
        Assert.False(PromakerShared.PlcVendorProfile.IsAidXgtVendor(PromakerShared.PlcVendorChoice.MicrexSx));

        // 벤더가 늘면 둘 중 하나로 분류해야 한다 — 이 개수 단정이 그 결정을 강제한다.
        Assert.Equal(5, Enum.GetValues<PromakerShared.PlcVendorChoice>().Length);
    }

    /// <summary>벤더를 바꿀 때 포트를 새 벤더 기본값으로 옮기는 판정 — 사용자가 손으로 넣은
    /// 포트를 덮어쓰면 안 되고, 반대로 남겨 두면 SX 를 골라도 LS 의 2004 로 붙는다.
    /// 열거를 손으로 적으면 벤더가 늘 때 조용히 낡으므로 enum 파생인지도 함께 지킨다.</summary>
    [Fact]
    public void IsAnyVendorDefaultPort_covers_every_vendor_and_nothing_else()
    {
        foreach (var vendor in Enum.GetValues<PromakerShared.PlcVendorChoice>())
        {
            var port = PromakerShared.PlcVendorProfile.Defaults(vendor).Port;
            Assert.True(
                PromakerShared.PlcVendorProfile.IsAnyVendorDefaultPort(port),
                $"{vendor} 의 기본 포트 {port} 가 기본값으로 인식되지 않는다");
        }

        // 사용자가 직접 넣었을 값 — 덮어쓰면 안 된다.
        Assert.False(PromakerShared.PlcVendorProfile.IsAnyVendorDefaultPort(502));
        Assert.False(PromakerShared.PlcVendorProfile.IsAnyVendorDefaultPort(2005));
        Assert.False(PromakerShared.PlcVendorProfile.IsAnyVendorDefaultPort(0));
        Assert.False(PromakerShared.PlcVendorProfile.IsAnyVendorDefaultPort(-1));

        // SX 기본 포트가 LS 와 다른 것이 이 판정의 존재 이유다.
        Assert.NotEqual(
            PromakerShared.PlcVendorProfile.Defaults(PromakerShared.PlcVendorChoice.LsXgi).Port,
            PromakerShared.PlcVendorProfile.Defaults(PromakerShared.PlcVendorChoice.MicrexSx).Port);
    }

    /// <summary>SX 는 잠긴 채로 시작해야 한다 — 쓰기 허용 영역이 비어 있으면 커넥터가 쓰기 권한
    /// 발급 자체를 거부한다. 기본 포트는 로더 인터페이스 서버 509.</summary>
    [Fact]
    public void MicrexSx_starts_write_locked_on_loader_port()
    {
        Assert.Equal(509, PromakerShared.PlcVendorProfile.Defaults(
            PromakerShared.PlcVendorChoice.MicrexSx).Port);

        var fresh = new PromakerShared.PlcConnectionSettings();
        Assert.Empty(fresh.SxWritableAreas);
        Assert.Equal(string.Empty, fresh.SxIoMapPath);
        Assert.Empty(new PlcSettings().SxWritableAreas);
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
