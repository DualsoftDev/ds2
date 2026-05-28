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
    public CctvSettings Cctv { get; set; } = new();
}

/// <summary>
/// CCTV 설정. RTSP 카메라를 MediaMTX(별도 Windows 서비스) 가 받아 WebRTC 로 재게시하고,
/// /cctv 페이지가 브라우저에서 WebRTC(WHEP) 로 시청한다.
/// 카메라 목록이 단일 진실 소스이며, 저장 시 DSPilot 이 MediaMTX 제어 API 로 경로를 동기화한다.
/// </summary>
public class CctvSettings
{
    /// <summary>MediaMTX 제어 API 주소 (DSPilot → localhost). 카메라 경로 등록/갱신용.</summary>
    public string MediaMtxApiUrl { get; set; } = "http://localhost:9997";

    /// <summary>
    /// 브라우저가 WebRTC(WHEP) 로 붙는 MediaMTX 포트. 실제 host 는 브라우저 접속 호스트로
    /// 클라이언트에서 치환되므로(원격 접속 대비) 포트만 보관한다.
    /// </summary>
    public int WebRtcPort { get; set; } = 8889;

    public List<CctvCamera> Cameras { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class CctvCamera
{
    /// <summary>MediaMTX 경로명 겸 화면 표시명. 영숫자/하이픈/언더스코어만 (URL path 로 사용).</summary>
    public string Name { get; set; } = "";

    /// <summary>원본 RTSP 주소. 예: rtsp://user:pass@192.168.0.10:554/stream1</summary>
    public string RtspUrl { get; set; } = "";

    public bool Enabled { get; set; } = true;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
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
    /// 사이클 최소 시간(ms). CT가 이 값 미만이면 비가동 사이클로 판정. 0이면 비활성.
    /// </summary>
    public int MinCycleTimeMs { get; set; } = 0;

    /// <summary>
    /// 개별 Call 최대 실행시간(ms). GoingTime이 이 값 초과 시 동작편차 통계에서 제외.
    /// </summary>
    public int MaxCallGoingTimeMs { get; set; } = 30000;

    /// <summary>
    /// 개별 Call 최소 실행시간(ms). GoingTime이 이 값 미만이면 동작편차 통계에서 제외. 0이면 비활성.
    /// </summary>
    public int MinCallGoingTimeMs { get; set; } = 0;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
