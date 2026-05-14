using System.Text.Json;
using System.Text.Json.Serialization;

namespace DSPilot.Models;

public class AppSettingsModel
{
    public DsPilotSettings DsPilot { get; set; } = new();
    public DatabaseSettings Database { get; set; } = new();
    public FlowCycleSettings FlowCycle { get; set; } = new();
    public PlcDatabaseSettings PlcDatabase { get; set; } = new();
    public DspTablesSettings DspTables { get; set; } = new();
    public HubSettings Hub { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();
    public UiSettings Ui { get; set; } = new();
    public HistoryViewSettings HistoryView { get; set; } = new();
}

/// <summary>
/// Promaker SignalHub 구독 설정 — 어느 Promaker 인스턴스(Control 5050 / Monitoring 5051 / 원격)
/// 에 client 로 붙을지 결정. 변경은 appsettings.json 저장 후 DSPilot 서비스 재시작 시 적용.
/// </summary>
public class HubSettings
{
    /// <summary>false 면 HubSubscriberService 자체를 등록하지 않음 (DI 단계에서 제외).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Promaker SignalHub URL. 기본 5051(Monitoring) — Control(5050) 구독 시 수동 변경.</summary>
    public string Url { get; set; } = "http://localhost:5051/hub/signal";

    /// <summary>수신을 받아들일 source 화이트리스트. 명시되지 않으면 control/virtualplant/plc 기본.</summary>
    public string[] AcceptedSources { get; set; } = ["control", "virtualplant", "plc"];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
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

    /// <summary>
    /// 사이클 시간 제한(ms). CT가 이 값 초과 시 비가동 사이클로 판정. 0이면 비활성.
    /// 가장 긴 Flow의 평균 CT + 여유시간으로 설정 권장.
    /// </summary>
    public int MaxCycleTimeMs { get; set; } = 0;

    /// <summary>
    /// 개별 Call 최대 실행시간(ms). GoingTime이 이 값 초과 시 동작편차 통계에서 제외.
    /// 기본 30000 (30초).
    /// </summary>
    public int MaxCallGoingTimeMs { get; set; } = 30000;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
