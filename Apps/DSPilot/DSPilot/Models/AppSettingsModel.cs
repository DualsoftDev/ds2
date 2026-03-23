using System.Text.Json;
using System.Text.Json.Serialization;

namespace DSPilot.Models;

public class AppSettingsModel
{
    public DsPilotSettings DsPilot { get; set; } = new();
    public DatabaseSettings Database { get; set; } = new();
    public PlcDatabaseSettings PlcDatabase { get; set; } = new();
    public PlcCaptureSettings PlcCapture { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();
    public UiSettings Ui { get; set; } = new();
}

public class DsPilotSettings
{
    public string AasxFilePath { get; set; } = "";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class PlcDatabaseSettings
{
    public int ReadIntervalMs { get; set; } = 100;
    public bool SimulationMode { get; set; }
    public string TagMatchMode { get; set; } = "Address";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class DatabaseSettings
{
    public string Type { get; set; } = "Sqlite";
    public string ConnectionString { get; set; } = "Data Source=%ProgramData%/DualSoft/DSPilot/plc.db;Version=3;BusyTimeout=20000";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class PlcCaptureSettings
{
    public bool Enabled { get; set; }
    public string PlcName { get; set; } = "MitsubishiPLC";
    public string PlcIpAddress { get; set; } = "192.168.0.1";
    public int PlcPort { get; set; } = 9002;
    public int ScanIntervalMs { get; set; } = 100;
    public string Protocol { get; set; } = "TCP";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class LoggingSettings
{
    public LogLevelSettings LogLevel { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class LogLevelSettings
{
    public string Default { get; set; } = "Information";

    [JsonPropertyName("Microsoft.AspNetCore")]
    public string MicrosoftAspNetCore { get; set; } = "Warning";

    [JsonPropertyName("DSPilot.Services")]
    public string DsPilotServices { get; set; } = "Debug";

    [JsonPropertyName("DSPilot.Repositories")]
    public string DsPilotRepositories { get; set; } = "Debug";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class UiSettings
{
    /// <summary>
    /// PLC 디버그 페이지 표시 여부
    /// </summary>
    public bool ShowPlcDebug { get; set; } = false;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
