using System;
using System.Collections.Generic;
using System.IO;
using Ds2.Core.StandardSubmodels;
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
                  "transport": "tcp",
                  "usbDeviceSelector": "",
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
                      "transport": "tcp",
                      "usbDeviceSelector": ""
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
                      "transport": "udp",
                      "usbDeviceSelector": ""
                    }
                  }
                }
                """);

            var settings = PromakerShared.PlcConnectionSettings.LoadOrDefault(path);

            Assert.Equal(100, settings.ScanIntervalMs);
            Assert.All(settings.Profiles.Values, profile => Assert.Equal(100, profile.ScanIntervalMs));
            Assert.Equal(PlcTransports.Udp, settings.Profiles["Mitsubishi"].Transport);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>SX 는 잠긴 채로 시작해야 한다 — 쓰기 허용 영역이 비어 있으면 커넥터가 쓰기 권한
    /// 발급 자체를 거부한다.</summary>
    [Fact]
    public void MicrexSx_starts_write_locked()
    {
        var fresh = new PromakerShared.PlcConnectionSettings();
        Assert.Empty(fresh.SxWritableAreas);
        Assert.Equal(string.Empty, fresh.SxIoMapPath);
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
