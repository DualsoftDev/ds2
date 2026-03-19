using System.Text.Json;
using System.Text.Json.Serialization;

namespace DSPilot.Models;

public class AppSettingsModel
{
    public DsPilotSettings DsPilot { get; set; } = new();
    public PlcDatabaseSettings PlcDatabase { get; set; } = new();
    public DspDatabaseSettings DspDatabase { get; set; } = new();
}

public class DsPilotSettings
{
    public string AasxFilePath { get; set; } = "";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class PlcDatabaseSettings
{
    public string SourceDbPath { get; set; } = "sample/db/DsDB.sqlite3";
    public int ReadIntervalMs { get; set; } = 1000;
    public bool SimulationMode { get; set; }
    public string TagMatchMode { get; set; } = "Address";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class DspDatabaseSettings
{
    public string Path { get; set; } = "%APPDATA%/DSPilot/dsp.db";
    public bool AutoCreate { get; set; } = true;
    public bool RecreateOnStartup { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
