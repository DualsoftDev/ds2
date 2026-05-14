using System.Text.Json;
using System.Text.Json.Serialization;

namespace DSPilot.Models;

public class AppSettingsModel
{
    public DatabaseSettings Database { get; set; } = new();
    public FlowCycleSettings FlowCycle { get; set; } = new();
    public DspTablesSettings DspTables { get; set; } = new();
    public HubSettings Hub { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();
    public UiSettings Ui { get; set; } = new();
    public HistoryViewSettings HistoryView { get; set; } = new();
}

/// <summary>
/// Promaker SignalHub 구독 설정. 일반 사용자가 만질 일이 없어 UI 노출은 제거됨 —
/// appsettings.json 직접 편집으로만 변경 가능. 변경은 DSPilot 서비스 재시작 시 적용.
/// </summary>
public class HubSettings
{
    public bool Enabled { get; set; } = true;
    public string Url { get; set; } = "http://localhost:5051/hub/signal";
    public string[] AcceptedSources { get; set; } = ["control", "virtualplant", "plc"];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class DatabaseSettings
{
    // Promaker 와 공유하는 ProgramData 경로 (SharedPaths.SharedDirectory 와 동일 폴더) — AASX/plc.db 공동 위치.
    public string ConnectionString { get; set; } = "Data Source=%ProgramData%/DualSoft/Shared/plc.db;Version=3;BusyTimeout=20000";

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
    public bool ShowPlcDebug { get; set; } = false;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class HistoryViewSettings
{
    /// <summary>
    /// 사이클 시간 제한(ms). CT가 이 값 초과 시 비가동 사이클로 판정. 0이면 비활성.
    /// </summary>
    public int MaxCycleTimeMs { get; set; } = 0;

    /// <summary>
    /// 개별 Call 최대 실행시간(ms). GoingTime이 이 값 초과 시 동작편차 통계에서 제외.
    /// </summary>
    public int MaxCallGoingTimeMs { get; set; } = 30000;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
