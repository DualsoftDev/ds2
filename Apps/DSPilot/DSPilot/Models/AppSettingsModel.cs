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
    public OeeSignalSettings OeeSignals { get; set; } = new();
    public ShiftSettings Shift { get; set; } = new();
}

/// <summary>
/// 작업자 시프트 운영 설정 (대시보드 "시프트 목표" 카드). 여러 작업자 화면이 같은 시프트를 보도록 서버에서 공유.
/// Start/End 는 로컬 벽시계 "HH:mm". End ≤ Start 이면 자정을 넘기는 야간 시프트로 해석.
/// 생산 목표는 (우선) 특정 Flow 1개 + 목표 카운트 — "만든 수" = 그 Flow 의 시프트 시작 이후 완료(비가동 제외) 사이클 수.
/// </summary>
public class ShiftSettings
{
    /// <summary>시프트 시작 시각 (로컬 "HH:mm").</summary>
    public string Start { get; set; } = "08:00";

    /// <summary>시프트 종료 시각 (로컬 "HH:mm"). Start 이하이면 익일로 넘어가는 야간 시프트.</summary>
    public string End { get; set; } = "17:00";

    /// <summary>시프트 종류 라벨: morning | afternoon | night (프리셋 선택 표식, 집계와 무관).</summary>
    public string ShiftType { get; set; } = "morning";

    /// <summary>생산 목표를 집계할 대상 Flow 명 (우선 단일 Flow). null/빈값이면 목표 미설정.</summary>
    public string? TargetFlow { get; set; }

    /// <summary>시프트 동안 만들어야 할 목표 개수.</summary>
    public int TargetCount { get; set; } = 0;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// OEE UserTag 자동수집 신호 매핑 (Flow별). OeeUserTagPollerService 가 raw plcTagLog 를 직접 쿼리해
/// onset/clear·생산/불량을 채운다. 미설정 Flow 는 해당 입력 자동수집 비활성(빈 채로 — 가짜값 금지).
/// (무사이클 정지검출 임계 등 튜닝 노브는 별도 "Oee" 섹션을 IConfiguration 으로 읽는다 — 혼동 방지 분리.)
/// </summary>
public class OeeSignalSettings
{
    public List<OeeFlowSignalMap> Flows { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class OeeFlowSignalMap
{
    public string FlowName { get; set; } = "";

    /// <summary>고장 비트 주소들. rising(0→1)=정지 onset(isFailure=1, unplanned), falling(1→0)=clear.</summary>
    public List<string> FaultBitAddresses { get; set; } = [];

    /// <summary>정지원인 비트. 고장 onset 직전 1인 비트의 ReasonCode 로 자동 분류(다중 1 시 Priority 최소 우선).</summary>
    public List<OeeCauseBit> CauseBits { get; set; } = [];

    /// <summary>생산수 신호. Kind=counter(누적 카운터 delta) | pulse(생산완료 펄스 rising edge 카운트).</summary>
    public OeeCounterSignal? ProductionCounter { get; set; }

    /// <summary>불량 신호. Kind=counter | pulse. 설정 시 manual reject 를 대체(plc &gt; manual).</summary>
    public OeeCounterSignal? RejectCounter { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class OeeCauseBit
{
    /// <summary>정지 사유 코드. 예: equipment_fault / material_wait / operator_wait / tooling.</summary>
    public string ReasonCode { get; set; } = "";

    public string Address { get; set; } = "";

    /// <summary>planned | unplanned. isFailure = (category == unplanned). 기본 unplanned.</summary>
    public string Category { get; set; } = "unplanned";

    /// <summary>다중 비트 동시 1 시 분류 우선순위. 값이 작을수록 우선.</summary>
    public int Priority { get; set; } = 0;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class OeeCounterSignal
{
    public string Address { get; set; } = "";

    /// <summary>counter | pulse.</summary>
    public string Kind { get; set; } = "counter";

    /// <summary>
    /// 누적 카운터 비트폭(16 | 32). 카운터 감소(wrap-around) 시 적용할 모듈러스를 결정.
    /// null(미지정)이면 wrap 을 추측하지 않고 감소를 리셋으로 처리 — 안전측(폭 추측으로 32-bit 카운터의
    /// 리셋을 16-bit wrap 으로 오판해 phantom 생산을 주입하는 것을 막는다). pulse 에는 무의미.
    /// </summary>
    public int? Width { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
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

    /// <summary>
    /// 표준(ideal) 사이클 시간(ms). P5 OEE Performance = (idealCT × totalCount) / runtime 의 단일 소스.
    /// null = 미설정(엔지니어 입력 또는 추정 전) → Performance 산출 불가로 정직 표기.
    /// doc/21 §2.4. (P2 표준편차 기준과 per-flow 차원에서만 공유 — §6 한정.)
    /// </summary>
    public int? IdealCycleTimeMs { get; set; }

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
