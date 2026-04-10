namespace Ds2.Core

open System

// =============================================================================
// SEQUENCE MONITORING SUBMODEL
// 역할: 현재 상태 실시간 추적
//   - Call/Work 현재 실행 상태 추적
//   - PLC 태그 실시간 값 모니터링
//   - Edge detection (신호 상승/하강 감지)
// 관계:
//   - 04_Logging.fs : Monitoring=현재(NOW)  / Logging=과거(PAST)
//   - 05_Maintenance.fs: Monitoring=실시간 알람 / Maintenance=에러 이력
// =============================================================================

// ─── Enums ───────────────────────────────────────────────────────────────────

/// Call 방향 (PLC 제어기 관점)
/// OutTag: PLC→장비 출력(DO) 명령 / InTag: 장비→PLC 입력(DI) 응답
type CallDirection = InOut=0 | InOnly=1 | OutOnly=2 | None=3

/// PLC 신호 Edge 타입
type EdgeType = RisingEdge=0 | FallingEdge=1 | NoChange=2

/// Edge 검출 모드
type EdgeDetectionMode = RisingEdgeOnly=0 | FallingEdgeOnly=1 | BothEdges=2

/// PLC 태그 데이터 타입
type TagDataType = Bool | Int16 | Int32 | Real | String

// ─── Real-time Data ───────────────────────────────────────────────────────────

/// PLC 태그 실시간 값
[<CLIMutable>]
type TagValue = {
    Name: string; Address: string; DataType: TagDataType
    Value: obj; Timestamp: DateTime
    Quality: string     // "Good" | "Bad" | "Uncertain"
}

/// Call 상태 전환 이벤트
type CallStateTransitionEvent = {
    CallName: string; FromState: Status4; ToState: Status4
    Timestamp: DateTime; DurationMs: float option
}

/// PLC 신호 Edge 감지 이벤트
type EdgeDetectionEvent = {
    CallName: string
    IsInTag: bool       // true=PLC 입력(DI), false=PLC 출력(DO)
    EdgeType: EdgeType; Timestamp: DateTime
}

/// 태그 Edge 상태 (immutable - TagStateTracker용)
[<CLIMutable>]
type TagEdgeState = {
    TagName: string; PreviousValue: string; CurrentValue: string
    LastUpdateTime: DateTime; EdgeType: EdgeType
}

/// 현재 실행 중인 Call 정보
type RunningCallInfo = {
    CallName: string; FlowName: string; State: Status4
    StartedAt: DateTime option; ElapsedMs: float option
    Progress: float     // 0.0 ~ 1.0
}

/// 현재 실행 중인 Work 정보
type RunningWorkInfo = {
    WorkName: string; FlowName: string; CallName: string option
    State: string; StartedAt: DateTime option; ElapsedMs: float option
    Progress: float     // 0.0 ~ 1.0
}

/// Flow 현재 상태 스냅샷
type FlowCurrentState = {
    FlowName: string; State: string; CurrentCycle: int
    RunningCalls: RunningCallInfo list
    RunningWorks: RunningWorkInfo list
    LastUpdated: DateTime
}

// ─── UI Data Models ───────────────────────────────────────────────────────────

/// 칸반 보드 아이템 (Call 상태별 분류 - DSPilot.Winform Kanban)
type KanbanItem = {
    CallName: string; FlowName: string; State: Status4
    WorkName: string option; Device: string option
    StartedAt: DateTime option; Priority: int; Tags: string list
}

/// Dashboard 타일 데이터 (DSPilot.Winform Dashboard)
type DashboardTile = {
    Title: string; Value: obj; Unit: string option
    Trend: string option        // "Up" | "Down" | "Stable"
    ColorClass: string          // "Success" | "Warning" | "Error"
    LastUpdated: DateTime
}

// ─── Properties ───────────────────────────────────────────────────────────────

/// System-level 모니터링 속성
type MonitoringSystemProperties() =
    inherit PropertiesBase<MonitoringSystemProperties>()
    // 메타데이터
    member val EngineVersion: string option = None        with get, set
    member val LangVersion:   string option = None        with get, set
    member val Author:        string option = None        with get, set
    member val DateTime: DateTimeOffset option = None     with get, set
    member val IRI:           string option = None        with get, set
    member val SystemType:    string option = None        with get, set

    // 실시간 모니터링 기본 설정
    member val EnableRealTimeMonitoring = true            with get, set
    member val MonitoringIntervalMs     = 100             with get, set
    member val EnableTagMonitoring      = true            with get, set
    member val TagRefreshIntervalMs     = 500             with get, set

    // ========== PLC 연결 정보 ==========
    member val EnablePlcMonitoring = false with get, set
    member val PlcIpAddress = "192.168.1.10" with get, set
    member val PlcPort = 5000 with get, set
    member val PlcType = Mitsubishi with get, set
    member val PlcProtocol = "MC Protocol" with get, set

    // ========== 폴링 (Polling) 설정 ==========
    member val EnablePolling = true with get, set
    member val PollingInterval = 1000 with get, set                 // 폴링 주기 (ms)

    // ========== 배치 읽기 최적화 ==========
    member val UseBatchRead = true with get, set
    member val BatchSize = 50 with get, set                         // 배치 크기 (태그 개수)

    // ========== 엣지 검출 설정 ==========
    member val EdgeDetectionMode = EdgeDetectionMode.RisingEdgeOnly with get, set
    member val DebounceTimeMs = 100 with get, set                   // 디바운스 시간 (ms)

    // ========== 데이터 저장 설정 ==========
    member val EnableAutoSave = true with get, set
    member val SaveInterval = 60 with get, set                      // 저장 주기 (초)
    member val DbConnectionString: string option = None with get, set

    // ========== 무결성 검증 설정 ==========
    member val EnableIntegrityCheck = false with get, set
    member val IntegrityCheckInterval = 3600 with get, set          // 검증 주기 (초, 1시간)
    member val ProgramChecksum: string option = None with get, set

    // ========== 알람 관리 설정 ==========
    member val EnableAlarmManagement = true with get, set
    member val AlarmRetentionDays = 30 with get, set                // 알람 보존 기간 (일)
    member val AutoAcknowledgeMinor = false with get, set           // 경미 알람 자동 확인

    // ========== 성능 추적 설정 ==========
    member val EnablePerformanceTracking = true with get, set
    member val PerformanceSnapshotInterval = 300 with get, set      // 스냅샷 주기 (초, 5분)

/// Flow-level 모니터링 속성
type MonitoringFlowProperties() =
    inherit PropertiesBase<MonitoringFlowProperties>()
    member val MonitoringTags  = ResizeArray<string>()    with get, set
    member val EnableAutoRefresh = true                   with get, set

/// Work-level 모니터링 속성
type MonitoringWorkProperties() =
    inherit PropertiesBase<MonitoringWorkProperties>()
    member val Motion:          string option = None      with get, set
    member val Script:          string option = None      with get, set
    member val ExternalStart    = false                   with get, set
    member val IsFinished       = false                   with get, set
    member val NumRepeat        = 0                       with get, set
    member val Duration:      TimeSpan option = None      with get, set
    member val SequenceOrder    = 0                       with get, set
    member val OperationCode:   string option = None      with get, set
    member val CurrentState:    string        = "Ready"   with get, set
    member val CurrentProgress: float         = 0.0       with get, set

/// Call-level 모니터링 속성
type MonitoringCallProperties() =
    inherit PropertiesBase<MonitoringCallProperties>()
    member val ObjectName:      string        = ""        with get, set
    member val ActionName:      string        = ""        with get, set
    member val RobotExecutable: string option = None      with get, set
    member val Timeout:       TimeSpan option = None      with get, set
    member val CallDirection:   string option = None      with get, set
    member val CurrentState:    Status4       = Status4.Ready with get, set
    member val LastStartedAt: DateTime option = None      with get, set
    member val CurrentProgress: float         = 0.0       with get, set

// ─── Helpers ─────────────────────────────────────────────────────────────────

module MonitoringHelpers =

    // ── CallDirection ─────────────────────────────────────────────────────────

    /// Tag 유무로 CallDirection 결정
    let determineCallDirection hasInTag hasOutTag =
        match hasInTag, hasOutTag with
        | true,  true  -> CallDirection.InOut
        | true,  false -> CallDirection.InOnly
        | false, true  -> CallDirection.OutOnly
        | false, false -> CallDirection.None

    // ── Edge Detection ────────────────────────────────────────────────────────

    /// 이전 값과 현재 값으로 EdgeType 감지
    let detectEdge prev cur =
        match prev, cur with
        | false, true  -> EdgeType.RisingEdge
        | true,  false -> EdgeType.FallingEdge
        | _            -> EdgeType.NoChange
