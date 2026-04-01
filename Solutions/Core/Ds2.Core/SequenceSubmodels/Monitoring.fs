namespace Ds2.Core

open System

/// Edge detection mode for PLC signal monitoring
type EdgeDetectionMode =
    | RisingEdgeOnly
    | FallingEdgeOnly
    | BothEdges
    | NoEdgeDetection


// ================================
// Monitoring domain value types
// ================================

/// PLC tag data with previous value for edge detection
[<Struct>]
type PlcTagData = {
    Address: string
    Value: bool
    PreviousValue: bool
}

/// PLC communication event (batch read result)
[<Struct>]
type PlcCommunicationEvent = {
    BatchTimestamp: DateTime
    Tags: PlcTagData array
    PlcName: string
}

    
// ================================
// Monitoring Properties Classes
// ================================


/// MonitoringFlowProperties - Flow-level 모니터링 속성
type MonitoringFlowProperties() =
    inherit PropertiesBase<MonitoringFlowProperties>()

    // ========== Flow 레벨 모니터링 설정 ==========
    member val FlowMonitoringEnabled = false with get, set

/// MonitoringSystemProperties - PLC 연결 및 데이터 수집
type MonitoringSystemProperties() =
    inherit PropertiesBase<MonitoringSystemProperties>()

    // ========== PLC 연결 정보 ==========
    member val PlcIpAddress: string option = None with get, set
    member val PlcPort: int option = None with get, set
    member val PlcType: string option = None with get, set      // "Mitsubishi" | "Siemens" | "AB"
    member val PlcProtocol: string option = None with get, set  // "MC Protocol" | "S7" | "EtherNet/IP"

    // ========== 폴링 설정 ==========
    member val EnablePolling = false with get, set
    member val PollingInterval = 1000 with get, set  // ms
    // NOTE: PollingTags 제거됨 (array는 MemberwiseClone shallow copy 위험)
    // → 필요시 별도 컬렉션으로 관리 (예: DsSystem.MonitoringTags: ResizeArray<string>)

    // ========== 배치 읽기 최적화 ==========
    member val UseBatchRead = false with get, set
    member val BatchSize = 50 with get, set

    // ========== 데이터베이스 연결 ==========
    member val DbConnectionString: string option = None with get, set
    member val EnableAutoSave = false with get, set
    member val SaveInterval = 60 with get, set  // seconds
    member val DbType = "PostgreSQL" with get, set

    // ========== 태그 매칭 ==========
    member val TagMatchMode = TagMatchMode.ByAddress with get, set
    member val PlcVendor: string option = None with get, set
    member val PlcModel: string option = None with get, set

/// MonitoringWorkProperties - Work-level 성능 추적
type MonitoringWorkProperties() =
    inherit PropertiesBase<MonitoringWorkProperties>()

    // ========== 성능 메트릭 ==========
    member val TrackCycleTime = false with get, set
    member val TargetCycleTime: float option = None with get, set
    member val TrackWaitTime = false with get, set
    member val TrackProcessTime = false with get, set

    // ========== 생산 추적 ==========
    member val CountProduction = false with get, set
    member val ProductionCounterTag: string option = None with get, set
    member val CountDefects = false with get, set
    member val DefectCounterTag: string option = None with get, set


/// MonitoringCallProperties - Call-level 성능 추적
type MonitoringCallProperties() =
    inherit PropertiesBase<MonitoringCallProperties>()
