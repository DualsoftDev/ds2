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
    public CycleExclusionSettings CycleExclusion { get; set; } = new();
    public AbnormalAlarmSettings AbnormalAlarm { get; set; } = new();
}

/// <summary>
/// 대시보드 이상감지 알람 배너 동작 설정.
/// </summary>
public class AbnormalAlarmSettings
{
    /// <summary>
    /// 알람 표시 유효 시간(시간). 이 시간보다 오래된 항목은 배너에서 제외.
    /// 0이면 비활성(전체 표시). 기본 24시간(하루).
    /// </summary>
    public int ResetIntervalHours { get; set; } = 24;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// 대시보드 "최근 사이클 히스토리"의 이상치 제외 필터 (Flow별 최소·최대 CT 범위). 여러 작업자 화면이
/// 같은 제외 기준을 공유하도록 클라이언트(브라우저)가 아닌 서버(appsettings)에 보관한다.
/// 관리 단위 = 초(seconds). CT 가 [MinSec, MaxSec] 밖이면 이상치로 제외. 특정 사이클 키가 아닌 "범위 규칙"이라
/// 새 사이클이 들어와도 그대로 적용되고, 사용자가 직접 해제(둘 다 비움)해야 풀린다.
/// </summary>
public class CycleExclusionSettings
{
    public List<FlowCycleExclusion> Ranges { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class FlowCycleExclusion
{
    public string FlowName { get; set; } = "";

    /// <summary>최소 CT(초). CT &lt; MinSec 이면 제외. null = 하한 없음.</summary>
    public double? MinSec { get; set; }

    /// <summary>최대 CT(초). CT &gt; MaxSec 이면 제외. null = 상한 없음.</summary>
    public double? MaxSec { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// 작업자 시프트 운영 설정 (대시보드 "시프트 목표" 카드). 여러 작업자 화면이 같은 시프트를 보도록 서버에서 공유.
/// Start/End 는 로컬 벽시계 "HH:mm". End ≤ Start 이면 자정을 넘기는 야간 시프트로 해석.
/// 생산 목표는 특정 Flow 1개 + 그 안의 Work 1개 + 목표 카운트 — "만든 수" = 그 Work 의 시프트 시작 이후
/// 완료(완료 신호 InTag↑ rising edge) 횟수. (<see cref="TargetWork"/> 미설정 시엔 Flow 사이클 수로 폴백 — 구버전 호환.)
/// </summary>
public class ShiftSettings
{
    /// <summary>시프트 시작 시각 (로컬 "HH:mm").</summary>
    public string Start { get; set; } = "08:00";

    /// <summary>시프트 종료 시각 (로컬 "HH:mm"). Start 이하이면 익일로 넘어가는 야간 시프트.</summary>
    public string End { get; set; } = "17:00";

    /// <summary>시프트 종류 라벨: morning | afternoon | night (프리셋 선택 표식, 집계와 무관).</summary>
    public string ShiftType { get; set; } = "morning";

    /// <summary>생산 목표를 집계할 대상 Flow 명 (Work 가 속한 Flow). null/빈값이면 목표 미설정.</summary>
    public string? TargetFlow { get; set; }

    /// <summary>
    /// 생산 목표를 집계할 대상 Work 명 (<see cref="TargetFlow"/> 안의 단일 Work). 설정 시 "만든 수" 는 이 Work 의
    /// 완료(InTag↑) 횟수. null/빈값이면 Flow 사이클 수로 폴백(구버전 호환).
    /// </summary>
    public string? TargetWork { get; set; }

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

    /// <summary>
    /// 외부(원격·클라우드) 접속용 공인 IP/도메인. 비우면 LAN 전용(기존 동작 그대로).
    /// 클라우드 VM 은 NIC 에 사설 IP(10.x/172.x)만 있고 공인 IP 는 1:1 NAT 로 매핑되므로,
    /// MediaMTX 가 NIC 에서 읽은 사설 IP 만 ICE 후보로 광고하면 외부 브라우저가 미디어에 못 닿는다.
    /// 이 값을 채우면 CctvMediaMtxService 가 MediaMTX 의 webrtcAdditionalHosts 에 반영해(+TCP 폴백 동반)
    /// 외부에서도 영상이 붙는다. 쉼표로 여러 개 가능. 예: "203.0.113.10" 또는 "cctv.example.com".
    /// </summary>
    public string WebRtcAdditionalHosts { get; set; } = "";

    public List<CctvCamera> Cameras { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class CctvCamera
{
    /// <summary>화면 표시명(자유 입력 — 한글 가능). MediaMTX 경로/URL 에는 쓰지 않는다(→ <see cref="Slug"/>).</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// MediaMTX 경로명 겸 WHEP URL path 세그먼트. ASCII 영숫자/하이픈/언더스코어만 — MediaMTX 가
    /// 비-ASCII 경로명을 거부하므로 표시명(<see cref="Name"/>, 한글 허용)과 분리한다.
    /// 카메라별로 안정(stable)해야 한다(표시명을 바꿔도 경로/오버레이가 흔들리지 않도록). 비우면
    /// <see cref="Services.CctvMediaMtxService.AssignSlugs"/> 가 표시명에서 자동 생성(불가 시 "cam") + 중복 회피.
    /// </summary>
    public string Slug { get; set; } = "";

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

    /// <summary>
    /// 대시보드 경로이탈 이상감지 알람 배너의 마퀴 텍스트 흐름 속도(px/초). 클수록 빠름. 기본 250.
    /// 여러 작업자 화면이 같은 속도를 보도록 서버(appsettings)에 보관 — 설정 페이지에서 변경,
    /// 저장 시 DatabaseRebuilt 브로드캐스트로 대시보드가 스냅샷을 재조회해 즉시 반영한다.
    /// </summary>
    public int AlarmMarqueeSpeedPxPerSec { get; set; } = 250;

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
    /// 대시보드 Flow 평균(AvgMT/WT/CT) 산출 시 집계할 "최근 비가동-제외 사이클 수"(롤링 윈도우).
    /// 요약 대시보드는 현재 거동을 반영해야 하므로 전(全)기간 누적 평균 대신 최근 N 사이클만 평균낸다.
    /// 0(또는 음수)이면 전체 이력 평균(윈도우 비활성). 기본 20.
    /// </summary>
    public int CycleAverageWindow { get; set; } = 20;

    /// <summary>
    /// 주기적 전체-이력 자동 재계산 간격(분). 라이브 기록기가 tail 완료를 실시간에 놓쳐
    /// WT 가 부풀려진 사이클을, 원시 plcTagLog 엣지에서 주기적으로 재도출해 self-heal 한다
    /// (사용자가 사이클 분석에서 "저장"을 누르는 것과 동일 경로). 0이면 비활성.
    /// </summary>
    public int AutoRecomputeIntervalMinutes { get; set; } = 10;

    /// <summary>
    /// 라이브 상태 self-heal 점검 간격(초). 엔진 in-memory Call 상태(정본)와 DB(dspCall/dspFlow)를
    /// 대조해, 쓰기 유실/지연으로 'Going'에 latch된 행을 교정한다(엔진이 non-Going 인데 DB가 Going 인 경우만).
    /// 자동 재계산의 무거운 트랜잭션이 라이브 다운그레이드 쓰기를 드롭시키는 경합을 흡수하는 안전망. 0이면 비활성.
    /// </summary>
    public int StateReconcileIntervalSeconds { get; set; } = 5;

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
