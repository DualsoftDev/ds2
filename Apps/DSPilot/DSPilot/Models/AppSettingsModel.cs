using System.Text.Json;
using System.Text.Json.Serialization;

namespace DSPilot.Models;

public class AppSettingsModel
{
    public DsPilotSettings DsPilot { get; set; } = new();
    public DatabaseSettings Database { get; set; } = new();
    public FlowCycleSettings FlowCycle { get; set; } = new();
    public PlcDatabaseSettings PlcDatabase { get; set; } = new();
    public PlcCaptureSettings PlcCapture { get; set; } = new();
    public DspTablesSettings DspTables { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();
    public UiSettings Ui { get; set; } = new();
    public HistoryViewSettings HistoryView { get; set; } = new();
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

public class FlowCycleSettings
{
    public List<FlowCycleOverride> Overrides { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class FlowCycleOverride
{
    public string FlowName { get; set; } = "";
    public string? StartCallName { get; set; }
    public string? EndCallName { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class PlcCaptureSettings
{
    public const string DefaultPlcName = "MitsubishiPLC";
    public const string DefaultPlcIpAddress = "192.168.0.1";
    public const int DefaultPlcPort = 9002;
    public const int DefaultScanIntervalMs = 100;
    public const string DefaultProtocol = "TCP";

    public bool Enabled { get; set; }
    public string PlcName { get; set; } = DefaultPlcName;
    public string PlcIpAddress { get; set; } = DefaultPlcIpAddress;
    public int PlcPort { get; set; } = DefaultPlcPort;
    public int ScanIntervalMs { get; set; } = DefaultScanIntervalMs;
    public string Protocol { get; set; } = DefaultProtocol;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class DspTablesSettings
{
    public bool Enabled { get; set; } = true;

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

public class HistoryViewSettings
{
    /// <summary>
    /// 필터 모드: "None" = 필터 없음, "Cycles" = 최근 N 사이클, "Days" = 최근 N일, "StartTime" = 특정 시작시간 이후
    /// </summary>
    public string FilterMode { get; set; } = "None";

    /// <summary>
    /// 표시할 최근 사이클 수 (FilterMode = "Cycles"일 때 사용)
    /// </summary>
    public int MaxCycles { get; set; } = 100;

    /// <summary>
    /// 표시할 최근 일수 (FilterMode = "Days"일 때 사용)
    /// </summary>
    public int MaxDays { get; set; } = 7;

    /// <summary>
    /// 시작 시간 (FilterMode = "StartTime"일 때 사용). 이 시간 이후의 히스토리만 표시
    /// </summary>
    public DateTime? StartTime { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
