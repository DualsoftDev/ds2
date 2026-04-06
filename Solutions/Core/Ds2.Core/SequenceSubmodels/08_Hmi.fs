namespace Ds2.Core

open System

// =============================================================================
// SEQUENCE HMI SUBMODEL
// Web 기반 인간-기계 인터페이스 (Human-Machine Interface)
// =============================================================================
//
// 목적:
//   Web 브라우저를 통한 Sequence 모델의 실시간 모니터링 및 수동 조작
//   - 계층적 조작: System → Flow → Work → Call → Device 단위 제어
//   - 실시간 상태 표시: 전체 시스템 상태를 Web UI로 시각화
//   - 버튼/램프/게이지: 다양한 UI 컴포넌트 자동 생성
//   - 화면 구성 정보: 레이아웃, 테마, 네비게이션 설정
//   - 권한 관리: 사용자별 조작 권한 제어
//
// 핵심 가치:
//   - 웹 접근성: 어디서나 브라우저로 접근 (모바일/태블릿/PC)
//   - 실시간 동기화: SignalR 기반 1초 이하 지연
//   - 직관적 UI: 색상/아이콘/애니메이션으로 상태 표현
//   - 안전 조작: 2단계 확인, 권한 체크, 조작 로그
//
// =============================================================================


// =============================================================================
// ENUMERATIONS - HMI 타입 정의
// =============================================================================

/// HMI 화면 레이아웃 타입
type HMILayoutType =
    | HMILayoutGrid                  // 그리드 레이아웃 (카드 기반)
    | HMILayoutFlow                  // 플로우 레이아웃 (프로세스 흐름)
    | HMILayoutTree                  // 트리 레이아웃 (계층 구조)
    | HMILayoutDashboard             // 대시보드 레이아웃 (위젯 기반)
    | HMILayoutCustom                // 커스텀 레이아웃 (사용자 정의)

/// UI 컴포넌트 타입
type UIComponentType =
    | Button                // 버튼 (클릭 동작)
    | Lamp                  // 램프 (상태 표시)
    | Gauge                 // 게이지 (아날로그 값)
    | Switch                // 스위치 (ON/OFF)
    | Slider                // 슬라이더 (값 조정)
    | TextInput             // 텍스트 입력
    | Dropdown              // 드롭다운 (선택)
    | Chart                 // 차트 (트렌드)

/// 버튼 동작 타입
type ButtonActionType =
    | Start                 // 시작
    | Stop                  // 정지
    | Pause                 // 일시정지
    | Resume                // 재개
    | Reset                 // 리셋
    | Ack                   // 확인 (알람)
    | Custom of string      // 커스텀 동작

/// 조작 레벨 (계층)
type OperationLevel =
    | HMISystemLevel           // System 전체 조작
    | HMIFlowLevel             // Flow 단위 조작
    | HMIWorkLevel             // Work 단위 조작
    | HMICallLevel             // Call 단위 조작
    | HMIDeviceLevel           // Device 세부 조작

/// 조작 권한 레벨
type PermissionLevel =
    | Viewer                // 보기만 가능
    | Operator              // 조작 가능
    | Engineer              // 설정 변경 가능
    | Admin                 // 모든 권한

/// 램프 색상 (상태 표시)
type LampColor =
    | Green                 // 정상 (Running)
    | Yellow                // 경고 (Warning)
    | Red                   // 에러 (Error)
    | Blue                  // 정보 (Info)
    | Gray                  // 비활성 (Disabled)

/// 테마
type HMITheme =
    | Light                 // 밝은 테마
    | Dark                  // 어두운 테마
    | Auto                  // 시스템 설정 따름


// =============================================================================
// VALUE TYPES
// =============================================================================

/// HMI 버튼 정의
type HMIButton() =
    member val ButtonId: Guid = Guid.NewGuid() with get, set
    member val Label: string = "" with get, set                     // 버튼 레이블
    member val Icon: string option = None with get, set             // 아이콘 (Font Awesome)
    member val ActionType: ButtonActionType = Start with get, set
    member val TargetId: Guid option = None with get, set           // 대상 Work/Flow/Call ID
    member val TargetLevel: OperationLevel = HMIWorkLevel with get, set
    member val RequireConfirmation = false with get, set            // 2단계 확인 필요
    member val ConfirmMessage: string option = None with get, set
    member val RequiredPermission: PermissionLevel = Operator with get, set
    member val Color: string = "primary" with get, set              // CSS 클래스
    member val IsEnabled = true with get, set
    member val CooldownSeconds = 0 with get, set                    // 연속 클릭 방지 (초)

/// HMI 램프 정의 (상태 표시)
type HMILamp() =
    member val LampId: Guid = Guid.NewGuid() with get, set
    member val Label: string = "" with get, set
    member val TargetId: Guid option = None with get, set           // 대상 Work/Flow ID
    member val TargetLevel: OperationLevel = HMIWorkLevel with get, set
    member val Color: LampColor = Gray with get, set
    member val IsBlinking = false with get, set                     // 깜빡임
    member val BlinkIntervalMs = 500 with get, set
    member val Tooltip: string option = None with get, set

/// HMI 게이지 정의 (아날로그 값)
type HMIGauge() =
    member val GaugeId: Guid = Guid.NewGuid() with get, set
    member val Label: string = "" with get, set
    member val Unit: string option = None with get, set             // 단위 (예: "°C", "%")
    member val MinValue = 0.0 with get, set
    member val MaxValue = 100.0 with get, set
    member val CurrentValue = 0.0 with get, set
    member val WarningThreshold: float option = None with get, set
    member val ErrorThreshold: float option = None with get, set
    member val GaugeType: string = "Linear" with get, set           // "Linear" | "Circular" | "Bar"

/// 화면 구성 정보 (레이아웃)
type ScreenLayout() =
    member val ScreenId: Guid = Guid.NewGuid() with get, set
    member val ScreenName: string = "" with get, set
    member val LayoutType: HMILayoutType = HMILayoutGrid with get, set
    member val Columns = 4 with get, set                            // Grid 레이아웃 시 열 개수
    member val Rows = 0 with get, set                               // 0 = auto
    member val Theme: HMITheme = Auto with get, set
    member val ShowHeader = true with get, set
    member val ShowFooter = true with get, set
    member val ShowSidebar = false with get, set

/// 네비게이션 메뉴 항목
type NavigationItem() =
    member val ItemId: Guid = Guid.NewGuid() with get, set
    member val Label: string = "" with get, set
    member val Icon: string option = None with get, set
    member val Route: string = "/" with get, set                    // URL 경로
    member val TargetLevel: OperationLevel option = None with get, set
    member val RequiredPermission: PermissionLevel = Viewer with get, set
    member val Children: NavigationItem array = [||] with get, set  // 하위 메뉴
    member val IsVisible = true with get, set

/// 조작 로그
type OperationLog() =
    member val LogId: Guid = Guid.NewGuid() with get, set
    member val Timestamp: DateTime = DateTime.UtcNow with get, set
    member val UserId: string = "" with get, set
    member val UserName: string = "" with get, set
    member val OperationType: string = "" with get, set             // "Start" | "Stop" | "Pause" | ...
    member val TargetId: Guid option = None with get, set
    member val TargetName: string = "" with get, set
    member val OperationLevel: OperationLevel = HMIWorkLevel with get, set
    member val Success = true with get, set
    member val ErrorMessage: string option = None with get, set

/// 실시간 상태 스냅샷 (SignalR 전송용)
type HMIStateSnapshot() =
    member val Timestamp: DateTime = DateTime.UtcNow with get, set
    member val SystemState: string = "Ready" with get, set          // "Ready" | "Running" | "Paused" | "Error"
    member val ActiveFlows: Guid array = [||] with get, set         // 실행 중인 Flow ID 목록
    member val ActiveWorks: Guid array = [||] with get, set         // 실행 중인 Work ID 목록
    member val AlarmCount = 0 with get, set
    member val CriticalAlarmCount = 0 with get, set
    member val CurrentCycleTime: float option = None with get, set  // 현재 사이클 타임 (초)


// =============================================================================
// PROPERTIES CLASSES
// =============================================================================

/// System-level HMI 속성
type HMISystemProperties() =
    inherit PropertiesBase<HMISystemProperties>()

    // ========== 기본 System 속성 ==========
    member val EngineVersion: string option = None with get, set
    member val LangVersion: string option = None with get, set
    member val Author: string option = None with get, set
    member val DateTime: DateTimeOffset option = None with get, set
    member val IRI: string option = None with get, set
    member val SystemType: string option = None with get, set

    // ========== HMI 활성화 ==========
    member val EnableHMI = true with get, set
    member val DefaultLayout = HMILayoutGrid with get, set
    member val DefaultTheme = Auto with get, set

    // ========== Web 서버 설정 ==========
    member val WebServerPort = 5000 with get, set
    member val EnableHttps = false with get, set
    member val EnableSignalR = true with get, set
    member val SignalRHub = "/hubs/hmi" with get, set

    // ========== 실시간 업데이트 ==========
    member val EnableRealtimeUpdate = true with get, set
    member val UpdateIntervalMs = 500 with get, set                 // 상태 업데이트 주기 (500ms)
    member val EnableAutoRefresh = true with get, set

    // ========== 화면 구성 ==========
    member val ShowSystemOverview = true with get, set
    member val ShowFlowList = true with get, set
    member val ShowAlarmPanel = true with get, set
    member val ShowPerformanceMetrics = true with get, set
    member val GridColumns = 4 with get, set

    // ========== 권한 관리 ==========
    member val EnablePermissionCheck = true with get, set
    member val RequireLoginForOperation = true with get, set
    member val DefaultPermission = Viewer with get, set
    member val EnableOperationLog = true with get, set

    // ========== 안전 조작 ==========
    member val EnableConfirmation = true with get, set              // 중요 동작 2단계 확인
    member val ConfirmCriticalOperations = true with get, set
    member val ButtonCooldownSeconds = 2 with get, set              // 버튼 연속 클릭 방지 (2초)

    // ========== 조작 로그 ==========
    member val OperationLogRetentionDays = 90 with get, set
    member val LogAllOperations = true with get, set

    // ========== UI 커스터마이징 ==========
    member val CustomCSS: string option = None with get, set
    member val CustomLogo: string option = None with get, set
    member val ApplicationTitle = "Sequence HMI" with get, set

/// Flow-level HMI 속성
type HMIFlowProperties() =
    inherit PropertiesBase<HMIFlowProperties>()

    member val EnableFlowHMI = true with get, set
    member val ShowFlowControl = true with get, set                 // Flow 제어 버튼 표시
    member val AllowManualStart = true with get, set
    member val AllowManualStop = true with get, set
    member val FlowIcon: string option = None with get, set
    member val FlowColor: string = "primary" with get, set

/// Work-level HMI 속성
type HMIWorkProperties() =
    inherit PropertiesBase<HMIWorkProperties>()

    // ========== 기본 Work 속성 ==========
    member val Motion: string option = None with get, set
    member val Script: string option = None with get, set
    member val ExternalStart = false with get, set
    member val IsFinished = false with get, set
    member val NumRepeat = 0 with get, set
    member val Duration: TimeSpan option = None with get, set
    member val SequenceOrder = 0 with get, set
    member val OperationCode: string option = None with get, set

    // ========== Work HMI 설정 ==========
    member val EnableWorkHMI = true with get, set
    member val ShowWorkControl = true with get, set                 // Work 제어 버튼 표시
    member val AllowManualTrigger = false with get, set             // Work 수동 트리거
    member val AllowSkip = false with get, set                      // Work 건너뛰기
    member val WorkIcon: string option = None with get, set
    member val WorkColor: string = "secondary" with get, set

    // ========== 상태 표시 ==========
    member val ShowDuration = true with get, set
    member val ShowProgress = true with get, set
    member val ShowStatus = true with get, set

/// Call-level HMI 속성
type HMICallProperties() =
    inherit PropertiesBase<HMICallProperties>()

    // ========== 기본 Call 속성 ==========
    member val ObjectName: string = "" with get, set
    member val ActionName: string = "" with get, set
    member val RobotExecutable: string option = None with get, set
    member val Timeout: TimeSpan option = None with get, set
    member val CallDirection: string option = None with get, set

    // ========== Call HMI 설정 ==========
    member val EnableCallHMI = true with get, set
    member val ShowCallControl = false with get, set                // Call 제어 버튼 (일반적으로 비활성)
    member val AllowManualExecution = false with get, set           // Call 수동 실행 (테스트용)
    member val ShowIOValues = true with get, set                    // I/O 값 표시

/// Device-level HMI 속성 (세부 수동 조작)
type HMIDeviceProperties() =
    inherit PropertiesBase<HMIDeviceProperties>()

    member val DeviceName: string = "" with get, set
    member val DeviceType: string = "Generic" with get, set         // "Motor" | "Cylinder" | "Valve" | ...
    member val EnableDeviceControl = false with get, set            // Device 직접 제어
    member val AllowManualMode = false with get, set                // 수동 모드 허용
    member val ShowDetailedStatus = true with get, set
    member val ControlButtons: HMIButton array = [||] with get, set // 세부 제어 버튼
    member val StatusLamps: HMILamp array = [||] with get, set      // 상태 램프
    member val Gauges: HMIGauge array = [||] with get, set          // 게이지


// =============================================================================
// HMI HELPERS
// =============================================================================

module HMIHelpers =

    // ========== 권한 체크 ==========

    /// 권한 레벨 비교 (높을수록 강함)
    let getPermissionLevel (permission: PermissionLevel) =
        match permission with
        | Admin -> 4
        | Engineer -> 3
        | Operator -> 2
        | Viewer -> 1

    /// 권한 검증
    let hasPermission (userPermission: PermissionLevel) (requiredPermission: PermissionLevel) =
        getPermissionLevel userPermission >= getPermissionLevel requiredPermission


    // ========== 상태 색상 매핑 ==========

    /// Work 상태 → 램프 색상
    let getStatusColor (status: Status4) =
        match status with
        | Status4.Ready -> Gray
        | Status4.Going -> Green
        | Status4.Finish -> Blue
        | Status4.Homing -> Yellow
        | _ -> Red

    /// 알람 심각도 → 색상
    let getAlarmColor (severity: string) =
        match severity with
        | "Critical" -> Red
        | "Error" -> Red
        | "Warning" -> Yellow
        | "Info" -> Blue
        | _ -> Gray


    // ========== 아이콘 매핑 ==========

    /// 버튼 동작 → Font Awesome 아이콘
    let getButtonIcon (actionType: ButtonActionType) =
        match actionType with
        | Start -> "fa-play"
        | Stop -> "fa-stop"
        | Pause -> "fa-pause"
        | Resume -> "fa-play-circle"
        | Reset -> "fa-redo"
        | Ack -> "fa-check"
        | Custom _ -> "fa-cog"


    // ========== 레이아웃 계산 ==========

    /// Grid 레이아웃 - 총 행 개수 계산
    let calculateGridRows (itemCount: int) (columns: int) =
        if columns > 0 then
            (itemCount + columns - 1) / columns
        else
            1

    /// 화면 크기별 Grid 열 개수 자동 조정
    let getResponsiveColumns (screenWidth: int) =
        if screenWidth < 768 then 1       // 모바일
        elif screenWidth < 1024 then 2    // 태블릿
        elif screenWidth < 1440 then 3    // 노트북
        else 4                            // 데스크탑


    // ========== 조작 검증 ==========

    /// 조작 가능 여부 확인
    let canOperate (button: HMIButton) (userPermission: PermissionLevel) (isSystemRunning: bool) =
        // 권한 체크
        if not (hasPermission userPermission button.RequiredPermission) then
            false, Some "권한 부족"
        // 버튼 활성화 상태 체크
        elif not button.IsEnabled then
            false, Some "버튼 비활성화"
        // Start 버튼은 시스템이 정지 상태일 때만
        elif button.ActionType = Start && isSystemRunning then
            false, Some "이미 실행 중"
        // Stop 버튼은 시스템이 실행 중일 때만
        elif button.ActionType = Stop && not isSystemRunning then
            false, Some "실행 중이 아님"
        else
            true, None


    // ========== SignalR 메시지 생성 ==========

    /// 상태 스냅샷 생성
    let createStateSnapshot (systemState: string) (activeFlows: Guid list) (alarmCount: int) =
        let snapshot = HMIStateSnapshot()
        snapshot.Timestamp <- DateTime.UtcNow
        snapshot.SystemState <- systemState
        snapshot.ActiveFlows <- List.toArray activeFlows
        snapshot.AlarmCount <- alarmCount
        snapshot

    /// 조작 로그 생성
    let createOperationLog (userId: string) (userName: string) (operation: string) (targetId: Guid option) (success: bool) =
        let log = OperationLog()
        log.Timestamp <- DateTime.UtcNow
        log.UserId <- userId
        log.UserName <- userName
        log.OperationType <- operation
        log.TargetId <- targetId
        log.Success <- success
        log


    // ========== UI 컴포넌트 생성 헬퍼 ==========

    /// 표준 Start 버튼 생성
    let createStartButton (targetId: Guid) (targetLevel: OperationLevel) =
        let button = HMIButton()
        button.Label <- "시작"
        button.Icon <- Some "fa-play"
        button.ActionType <- Start
        button.TargetId <- Some targetId
        button.TargetLevel <- targetLevel
        button.RequireConfirmation <- true
        button.ConfirmMessage <- Some "시작하시겠습니까?"
        button.RequiredPermission <- Operator
        button.Color <- "success"
        button

    /// 표준 Stop 버튼 생성
    let createStopButton (targetId: Guid) (targetLevel: OperationLevel) =
        let button = HMIButton()
        button.Label <- "정지"
        button.Icon <- Some "fa-stop"
        button.ActionType <- Stop
        button.TargetId <- Some targetId
        button.TargetLevel <- targetLevel
        button.RequireConfirmation <- true
        button.ConfirmMessage <- Some "정지하시겠습니까?"
        button.RequiredPermission <- Operator
        button.Color <- "danger"
        button

    /// 상태 램프 생성
    let createStatusLamp (label: string) (targetId: Guid) (color: LampColor) =
        let lamp = HMILamp()
        lamp.Label <- label
        lamp.TargetId <- Some targetId
        lamp.Color <- color
        lamp
