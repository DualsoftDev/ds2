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
