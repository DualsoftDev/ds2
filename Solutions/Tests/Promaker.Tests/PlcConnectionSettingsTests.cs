using System;
using System.IO;
using Promaker.ViewModels;
using PromakerShared = Promaker.Shared;
using Xunit;

namespace Promaker.Tests;

public sealed class PlcConnectionSettingsTests
{
    [Fact]
    public void Defaults_use_50ms_batch_scan_interval_for_monitoring()
    {
        var settings = new PromakerShared.PlcConnectionSettings();
        settings.EnsureProfiles();

        Assert.Equal(50, PromakerShared.PlcConnectionSettings.DefaultScanIntervalMs);
        Assert.Equal(50, settings.ScanIntervalMs);
        Assert.All(settings.Profiles.Values, profile => Assert.Equal(50, profile.ScanIntervalMs));
        Assert.Equal(50, new PlcSettings().ScanIntervalMs);
    }

    [Fact]
    public void LoadOrDefault_upgrades_previous_100ms_default_interval()
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
                  "scanIntervalMs": 100,
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
                      "scanIntervalMs": 100,
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
                      "scanIntervalMs": 100,
                      "localEthernet": true,
                      "networkNumber": 0,
                      "stationNumber": 255,
                      "isUdp": false
                    }
                  }
                }
                """);

            var settings = PromakerShared.PlcConnectionSettings.LoadOrDefault(path);

            Assert.Equal(50, settings.ScanIntervalMs);
            Assert.All(settings.Profiles.Values, profile => Assert.Equal(50, profile.ScanIntervalMs));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
