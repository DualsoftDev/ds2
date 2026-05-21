using Ev2.Backend.Common;
using Ev2.Backend.PLC;
using Ev2.PLC.Protocol.LS;
using Ev2.PLC.Protocol.MX;
using Microsoft.Extensions.Configuration;

namespace DSPilot.TestConsole;

internal class MitsubishiPlcSettings
{
    public string IpAddress { get; set; } = "192.168.9.120";
    public int Port { get; set; } = 5555;
    public string Name { get; set; } = "MitsubishiPLC";
}

internal class LsPlcSettings
{
    public string IpAddress { get; set; } = "192.168.9.100";
    public int Port { get; set; } = 2004;
    public string Name { get; set; } = "LSPLC";
    /// <summary>XGI / XGK / XGT</summary>
    public string PlcModel { get; set; } = "XGI";
}

internal class PlcConnectionSettings
{
    /// <summary>Mitsubishi 또는 LS</summary>
    public string PlcType { get; set; } = "Mitsubishi";
    public MitsubishiPlcSettings Mitsubishi { get; set; } = new();
    public LsPlcSettings LS { get; set; } = new();

    private bool IsLS => PlcType.Equals("LS", StringComparison.OrdinalIgnoreCase);

    public static PlcConnectionSettings FromConfig(IConfiguration config)
    {
        var settings = new PlcConnectionSettings();
        config.GetSection("PlcConnection").Bind(settings);
        return settings;
    }

    public ScanConfiguration CreateScanConfig(Ev2.PLC.Common.TagSpecModule.TagSpec[] tagSpecs)
    {
        if (IsLS)
        {
            var lsModel = LS.PlcModel switch
            {
                "XGK" => LsPlcModel.XGK,
                "XGT" => LsPlcModel.XGT,
                _ => LsPlcModel.XGI,
            };
            var lsConfig = new LsConnectionConfig
            {
                IpAddress = LS.IpAddress,
                Port = LS.Port,
                Name = LS.Name,
                PlcModel = lsModel,
                EnableScan = true,
                Timeout = TimeSpan.FromSeconds(5),
                ScanInterval = TimeSpan.FromMilliseconds(500),
            };
            return new ScanConfiguration { Connection = lsConfig, TagSpecs = tagSpecs };
        }
        else
        {
            var mxConfig = new MxConnectionConfig
            {
                IpAddress = Mitsubishi.IpAddress,
                Port = Mitsubishi.Port,
                Name = Mitsubishi.Name,
                EnableScan = true,
                Timeout = TimeSpan.FromSeconds(5),
                ScanInterval = TimeSpan.FromMilliseconds(500),
                FrameType = FrameType.QnA_3E_Binary,
                Protocol = TransportProtocol.UDP,
                AccessRoute = new AccessRoute(0, 255, 1023, 0),
                MonitoringTimer = 16,
            };
            return new ScanConfiguration { Connection = mxConfig, TagSpecs = tagSpecs };
        }
    }

    public string DisplayName => IsLS
        ? $"LS ({LS.IpAddress}:{LS.Port}, {LS.PlcModel})"
        : $"Mitsubishi ({Mitsubishi.IpAddress}:{Mitsubishi.Port})";
}
