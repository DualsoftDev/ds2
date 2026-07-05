// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Text.Json;
using System.Text.Json.Serialization;
using DSPilot.Infrastructure;

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
    public OeeManualSettings OeeManual { get; set; } = new();
    public ShiftSettings Shift { get; set; } = new();
    public CycleExclusionSettings CycleExclusion { get; set; } = new();
    public AbnormalAlarmSettings AbnormalAlarm { get; set; } = new();
    public AutoCalibrationSettings AutoCalibration { get; set; } = new();
}

/// <summary>
/// OEE 사용자 수동 입력값(불량 카운트 자동수집과 별개). doc/21 §12.
/// 작은 라인에서 "이 생산의 전반적 양품률은 대략 N%" 를 직접 지정하는 단순 오버라이드.
/// </summary>
public class OeeManualSettings
{
    /// <summary>
    /// 사용자가 직접 설정한 전반 품질(양품률) %. 0~100. null = 미설정 → 불량 입력 기반(measured) 또는 100% 가정(assumed)으로 폴백.
    /// 설정 시 라인·전 설비 OEE 의 품질(Q)에 그대로 적용된다(QualitySource="manual").
    /// </summary>
    public double? QualityPercent { get; set; }

    /// <summary>
    /// 사용자가 직접 설정한 <b>비생산 시간대</b>(반복 일일, 라인 전체). 이 시간대에 발생한 비가동(사이클 비가동·무사이클)은
    /// 비생산으로 분류되어 가용성(A) 분모에서 제외된다(표준 OEE — 비생산은 가용성을 깎지 않음).
    /// <see cref="PlannedStopsAuto"/> 가 꺼졌을(수동) 때만 적용된다.
    /// </summary>
    public List<PlannedStopWindow> PlannedStops { get; set; } = [];

    /// <summary>
    /// 비생산 시간대 <b>자동 계산</b> on/off. true = 무변화 정지 길이 ≥ 10×(14일 평균 CT) 인 구간을 비생산으로 자동 분류
    /// (시각대 윈도 없이 지속시간만으로 판정 — 고장신호·이상감지와 무관한 순수 CT 기반, doc/22 §3.3). false = 사용자가
    /// 직접 그린 <see cref="PlannedStops"/> 시각대만 적용. <b>수동 적용(시간대 저장) 시 자동으로 false 로 내려가고</b>,
    /// 사용자가 '자동 계산'을 다시 켜면 true 로 복귀한다.
    /// null = 레거시(미설정) → <see cref="PlannedStops"/> 유무로 추론(있으면 수동=false, 없으면 자동=true) — 기존 설치 호환.
    /// </summary>
    public bool? PlannedStopsAuto { get; set; }

    /// <summary>현재 비생산 자동계산이 켜져 있는지(레거시 null 은 시간대 유무로 추론). 단일 판정 소스.</summary>
    [JsonIgnore]
    public bool PlannedStopsAutoEffective => PlannedStopsAuto ?? (PlannedStops is not { Count: > 0 });

    /// <summary>자동 비생산 패턴 캐시 (자동 모드 전환 또는 24h 만료 시 갱신). null = 아직 미계산.</summary>
    public PlannedAutoPatternCache? AutoPatternCache { get; set; }

    /// <summary>
    /// 대시보드 "가동횟수" 카드의 <b>출력(생산) 기준 Flow</b> 지정. flow별 사이클수를 그냥 합치면 직렬(파이프라인)
    /// 공정에서 같은 제품이 여러 flow를 지나며 공정 단계 수만큼 과다 계상되므로, "완제품 1개 = 1사이클"인
    /// flow(들)를 지정해 그 합만 산출량으로 보여준다(지정 시 그 flow들의 사이클수 합).
    /// 비어 있으면 <b>자동(평균) 모드</b>: 전체 flow 사이클수 합 ÷ (기간 내 가동한 flow 수)를 정수 평균으로 표시.
    /// </summary>
    public List<string> OutputFlows { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// 자동 비생산 시간대 패턴 캐시. 자동 모드 전환 시 또는 24h 만료 후 재계산.
/// 학습 = 일별 샘플 투표제(doc/22 §3.5, OeeNonProdPatternService): 활동일마다 장시간(10×) 정지 구간을
/// 30분 슬롯에 페인팅해 1표, 활동일의 60% 이상 반복된 슬롯만 창으로 승격. 참고 표시 전용(KPI 미적용).
/// </summary>
public class PlannedAutoPatternCache
{
    public List<PlannedStopWindow> Windows { get; set; } = [];
    public DateTime ComputedAt { get; set; }
    public DateTime DataFrom { get; set; }
    public DateTime DataTo { get; set; }

    /// <summary>투표 분모가 된 활동일(사이클 ≥1) 수 — 표본 신뢰도 표시용.</summary>
    public int ActiveDays { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// 계획정지 시간대 한 칸 — 반복 일일 로컬 시각 구간(자정 기준 분, [Start, End)). 라인 전체 적용.
/// 예: 점심 12:00–13:00 = (720, 780). 자정 넘김은 두 칸으로 분리 입력.
/// </summary>
public class PlannedStopWindow
{
    /// <summary>시작(로컬 자정 기준 분, 0~1439).</summary>
    public int StartMinutes { get; set; }

    /// <summary>끝(로컬 자정 기준 분, 1~1440, Start &lt; End).</summary>
    public int EndMinutes { get; set; }

    /// <summary>표시용 라벨(예: "점심", "정기정비"). 선택.</summary>
    public string? Label { get; set; }
}

/// <summary>
/// 실측 duration 자동 보정(auto-calibration) 설정. 첫 설치 후 각 Flow 가 이상치 제외 클린사이클을
/// <see cref="MinCleanCycles"/> 개 이상 모으면, 그 Flow 의 디바이스(Device Work) Duration/Min/MaxDuration 을
/// 실측값으로 1회 자동 채운다(<see cref="Services.AutoCalibrationService"/>). 공식:
///   Duration = round(mean),
///   Max = round(max(중앙값 × (1 + <see cref="MedianMarginMaxPct"/>), 클린 실측최대)) + <see cref="MarginMaxAbsMs"/>,
///   Min(<see cref="FillMin"/>=true 일 때만) = round(p<see cref="PercentileMin"/> × (1 − <see cref="MarginMinPct"/>)).
/// <see cref="CompletedAt"/> 가 1회성 플래그 — Production.json 에 영속되어 재설치/재시작 시 보존된다.
/// </summary>
public class AutoCalibrationSettings
{
    /// <summary>자동 보정 사용 여부. 끄면 백그라운드 자동 실행은 멈추지만 수동 "지금 실측값 채우기" 는 동작한다.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>이 개수 이상의 이상치 제외 클린사이클(IsIdle=0 AND CT NOT NULL)을 모은 Flow 만 보정한다. 기본 10.</summary>
    public int MinCleanCycles { get; set; } = 10;

    /// <summary>
    /// MaxDuration 여유율(분수, 중앙값 대비). Max = round(max(중앙값 × (1 + 이 값), 클린 실측최대)) + <see cref="MarginMaxAbsMs"/>.
    /// 기본 0.60(=+60%). 클린 실측최대(중앙값×3 초과 이상치 제외) 클램프 덕에 이미 관측된 정상 가동은 임계 안쪽에 남는다.
    /// (구 MaxMode/PercentileMax/MarginMaxPct 3필드를 대체 — 옛 키는 ExtensionData 로 무해하게 흡수.)
    /// </summary>
    public double MedianMarginMaxPct { get; set; } = 0.60;

    /// <summary>MaxDuration 절대 추가 여유(ms). Max = (모드 산출값) + 이 값. "정상보다 N초 더 걸리면 정지" 절대 버퍼. 기본 5000(=5초).</summary>
    public int MarginMaxAbsMs { get; set; } = 5000;

    /// <summary>true 일 때만 MinDuration 을 실측값으로 기록(false 면 기존값 보존). 기본 false.</summary>
    public bool FillMin { get; set; } = false;

    /// <summary>MinDuration 기준 백분위수(0-100). Min = round(p5 × (1−MarginMinPct)) 기본.</summary>
    public double PercentileMin { get; set; } = 5.0;

    /// <summary>MinDuration 추가 여유율(분수). Min = round(pN × (1 − 이 값)). 기본 0.03(=−3%).</summary>
    public double MarginMinPct { get; set; } = 0.03;

    /// <summary>
    /// 자동 보정을 1회 실행 완료한 UTC 시각(1회성 트리거 플래그). null 이면 아직 미실행 — Enabled &amp;&amp; CompletedAt==null
    /// 일 때만 자동 실행한다. 최초 성공 시 한 번만 채워져 재시작 시 재실행을 막는다(새 PC 는 Production.json 이 없어
    /// null 이라 다시 실행). 이후 수동 재실행으로 다시 기록해도 이 값은 바뀌지 않는다(아래 <see cref="LastAppliedAt"/> 가 갱신).
    /// </summary>
    public DateTime? CompletedAt { get; set; } = null;

    /// <summary>
    /// 마지막으로 실제 project.aasx 에 실측 duration 을 기록한 UTC 시각. 자동/수동 무관히 적용될 때마다 갱신된다
    /// (= AASX 가 실측값으로 수정된 시각). <see cref="CompletedAt"/>(최초 1회 고정)과 달리 매 적용마다 최신화 — 설정
    /// 페이지가 "마지막 실측 적용" 으로 표시한다. null 이면 아직 한 번도 기록되지 않음.
    /// </summary>
    public DateTime? LastAppliedAt { get; set; } = null;

    /// <summary>마지막 적용 요약(예: "Flow 2/3개 보정, 디바이스 5건 기록 [...]"). 표시용. 미기록이면 null.</summary>
    public string? LastAppliedSummary { get; set; } = null;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
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

    /// <summary>
    /// 배너에 표시할 알람 레벨 집합(Info/Warning/Error 중). 비어 있으면 전체 표시.
    /// usertag 알람이 모든 레벨로 쏟아지는 것을 막기 위해 기본은 Error 만 — 경로이탈 4종은 전부 Error 라 기본값에서도 그대로 보인다.
    /// 설정 페이지(이상 알람 배너)에서 선택, active-alarms 피드(배너·uptime 띠)에서 읽기 시 필터.
    /// </summary>
    public List<string> DisplayLevels { get; set; } = ["Error"];

    /// <summary>
    /// 디바이스별 이상감지 차단 규칙. 규칙에 걸린 (디바이스, 유형) 이상은
    /// 신규 발생 시 어디에도 기록되지 않고(링버퍼/userTagAlertLog/SignalR 생략),
    /// 차단 이전의 기존 기록도 알람·사이드바·통계·기록 조회에서 숨겨진다(읽기 시 필터 — 해제하면 다시 표시).
    /// </summary>
    public List<AbnormalDeviceFilter> DeviceFilters { get; set; } = [];

    /// <summary>
    /// 사용자정의(UserTag) 알람 차단 목록 — 차단할 UserTag 정의의 TagAddress(정의 고유키).
    /// 규칙에 걸린 UserTag 는 신규 발생 시 어디에도 기록되지 않고(라이브 큐/userTagAlertLog/SignalR 생략),
    /// 차단 이전의 기존 기록도 알람·사이드바·통계·기록 조회에서 숨겨진다(읽기 시 필터 — 해제하면 다시 표시).
    /// 자동감지(Abnormal) 차단(<see cref="DeviceFilters"/>)의 UserTag 대응물.
    /// </summary>
    public List<string> UserTagFilters { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// 디바이스 1개의 이상감지 차단 규칙. 디바이스 = Call 이름 "{DevicesAlias}.{ApiName}" 의 DevicesAlias 부분.
/// </summary>
public class AbnormalDeviceFilter
{
    public string Device { get; set; } = "";

    /// <summary>차단할 AbnormalKind int 값 목록 (SensorOpen=0, SensorShort=1, ActionOver=2, ActionUnder=3). 비면 규칙 무효(저장 시 제거).</summary>
    public List<int> Kinds { get; set; } = [];

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

    /// <summary>
    /// 사용자가 시프트(Start/End)를 명시적으로 설정했는지. 기본 false — 코드 기본값 08:00/17:00 이 파일에 박제돼도
    /// "미설정"으로 구분하기 위함(<see cref="EnsureSettingsFiles"/>). OEE 가용성 분모 폴백 체인에서
    /// true 일 때만 시프트를 권위적 계획시간으로 쓴다(false 면 14일 자동추정 ▸ 달력근사로 폴백). doc/21 §12.
    /// 대시보드 시프트 목표 카드(TargetFlow/만든수)와는 무관 — 그쪽 동작은 이 플래그를 보지 않는다.
    /// </summary>
    public bool UserSet { get; set; } = false;

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

    /// <summary>
    /// 무조작 일시정지(절전 가드) 사용 여부. 현장 카메라가 LTE 종량 회선으로 RTSP 를 올리는
    /// 사이트 보호용 — 브라우저 입력이 <see cref="IdlePauseMinutes"/> 동안 없으면 시청을 멈춰
    /// MediaMTX(sourceOnDemand)가 카메라 RTSP 연결을 닫게 한다. 적용은 클라이언트(cctv-whep.js).
    /// 탭 숨김 시 정지는 이 설정과 무관하게 항상 동작한다(숨긴 탭의 시청은 항상 낭비).
    /// </summary>
    public bool IdlePauseEnabled { get; set; } = true;

    /// <summary>무조작 일시정지까지의 시간(분). 기본 60분.</summary>
    public int IdlePauseMinutes { get; set; } = 60;

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

    /// <summary>
    /// 대체(폴백) 이미지의 서빙 URL (예 "/uploads/cctv-fallbacks/cam1.jpg?v=...").
    /// CCTV 연결 실패·주소 없음·대기 시 대시보드/영상벽이 라이브 영상 대신 이 정지 이미지를 표시한다.
    /// 비우면 대체 이미지 없음(기존 "연결 실패/연결 중" 안내를 그대로 표시). 파일 입출력은
    /// <see cref="Services.CctvFallbackImageService"/> 가, 이 연결값의 영속은 설정 저장 라운드트립이 담당한다.
    /// </summary>
    public string FallbackImage { get; set; } = "";

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
    // Promaker 와 공유하는 경로 (SharedPaths.SharedDirectory 와 동일 폴더) — AASX/plc.db 공동 위치.
    // SharedPaths 단일 소스에서 유도해 Windows(%ProgramData%\DualSoft\Shared)·Linux(DUALSOFT_SHARED_DIR
    // 오버라이드) 양쪽에서 project.aasx 와 항상 같은 디렉터리로 정합된다. Version/BusyTimeout 토큰은 과거
    // 호환용 표기로, 실제 접근은 DatabaseConfigLoader 가 Data Source 만 추출해 재구성한다.
    public string ConnectionString { get; set; } =
        $"Data Source={Path.Combine(SharedPaths.SharedDirectory, "plc.db")};Version=3;BusyTimeout=20000";

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
    /// null = 미설정(엔지니어 입력 또는 자동기입 전) → Performance 산출 불가로 정직 표기.
    /// doc/21 §2.4. (P2 표준편차 기준과 per-flow 차원에서만 공유 — §6 한정.)
    /// </summary>
    public int? IdealCycleTimeMs { get; set; }

    /// <summary>
    /// <see cref="IdealCycleTimeMs"/> 의 출처. "auto" = <see cref="Services.OeeIdealCycleAutoFillService"/> 가
    /// 실측(best-demonstrated 분위수)으로 자동 기입한 값. null = 사람이 입력(또는 구버전 데이터 — 자동기입 도입 전
    /// 값은 전부 수동이므로 null=manual 해석이 하위호환). 사용자가 값을 직접 저장/해제하면 null 로 돌아간다 —
    /// 값을 비우면 자동기입이 다음 주기에 다시 채울 수 있다(재보정 경로).
    /// </summary>
    public string? IdealCycleTimeSource { get; set; }

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
    /// 대시보드 경로이탈 이상감지 알람 배너의 세로 티커 전환 간격(초). 한 건이 머무는 시간 — 작을수록 빠르게 넘어간다. 기본 3초.
    /// 여러 작업자 화면이 같은 속도를 보도록 서버(appsettings)에 보관 — 설정 페이지에서 변경,
    /// 저장 시 DatabaseRebuilt 브로드캐스트로 대시보드가 스냅샷을 재조회해 즉시 반영한다.
    /// </summary>
    public int AlarmTickerIntervalSec { get; set; } = 3;

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

    /// <summary>
    /// 동작편차 색상 범례 "주의" 임계(편차 %, = CV×100). 편차가 이 값 이상이면 주의(노랑). 기본 10%.
    /// </summary>
    public double HeatmapCautionPct { get; set; } = 10.0;

    /// <summary>
    /// 동작편차 색상 범례 "위험" 임계(편차 %, = CV×100). 편차가 이 값 초과면 위험(빨강). 기본 30%.
    /// 항상 <see cref="HeatmapCautionPct"/> 보다 커야 한다(저장 시 서버에서 보정).
    /// </summary>
    public double HeatmapDangerPct { get; set; } = 30.0;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
