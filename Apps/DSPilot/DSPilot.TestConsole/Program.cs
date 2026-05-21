using DSPilot.TestConsole;
using Microsoft.Extensions.Configuration;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .Build();

var plcSettings = PlcConnectionSettings.FromConfig(config);
var defaultAasxPath = config["AasxPath"] ?? @"C:\ds\ds2\Apps\DSPilot\DsCSV_0318_C.aasx";

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("╔════════════════════════════════════════════╗");
Console.WriteLine("║  DSPilot Test Console - AASX Flow Sim      ║");
Console.WriteLine("╚════════════════════════════════════════════╝");
Console.WriteLine($"  PLC: {plcSettings.DisplayName}");
Console.WriteLine($"  AASX default: {defaultAasxPath}");
Console.WriteLine();

await FlowSimulationTest.RunAsync(plcSettings, defaultAasxPath);
