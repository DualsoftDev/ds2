namespace Ds2.Core

open System
open System.Text.Json.Serialization

/// PLC data type for tag mapping
type PlcDataType =
    | Bool
    | Int16
    | UInt16
    | Int32
    | UInt32
    | Float32
    | Float64
    | String of maxLength:int

/// Call direction for I/O tag mapping
type CallDirection =
    | InOut   // InTag + OutTag both exist
    | InOnly  // InTag only exists
    | OutOnly // OutTag only exists
    | NoMapping    // No PLC mapping

/// Tag matching mode for PLC integration
type TagMatchMode =
    | ByAddress  // Address-based matching (recommended)
    | ByName     // Name-based matching

/// Name transformation for tag generation
type NameTransform =
    | UpperCase
    | LowerCase
    | CamelCase
    | PascalCase

// ================================
// Control domain value types
// ================================
type IOTag() =
    member val Name : string = "" with get, set
    member val Address : string = "" with get, set
    member val Description : string = "" with get, set
    new(name: string, addr: string, desc: string) as this = IOTag() then
        this.Name <- name
        this.Address <- addr
        this.Description <- desc
    
// =============================================================================
// Hardware Component
// =============================================================================

[<AbstractClass>]
type HwComponent(name, parentId) =
    inherit DsChild(name, parentId)
    member val InTag     : IOTag option = None with get, set
    member val OutTag    : IOTag option = None with get, set
    member val FlowGuids = ResizeArray<Guid>() with get, set


type HwButton    [<JsonConstructor>] internal (name, parentId) =
    inherit HwComponent(name, parentId)
    member this.DeepCopy() = DeepCopyHelper.jsonCloneEntity this

type HwLamp      [<JsonConstructor>] internal (name, parentId) =
    inherit HwComponent(name, parentId)
    member this.DeepCopy() = DeepCopyHelper.jsonCloneEntity this

type HwCondition [<JsonConstructor>] internal (name, parentId) =
    inherit HwComponent(name, parentId)
    member this.DeepCopy() = DeepCopyHelper.jsonCloneEntity this

type HwAction    [<JsonConstructor>] internal (name, parentId) =
    inherit HwComponent(name, parentId)
    member this.DeepCopy() = DeepCopyHelper.jsonCloneEntity this

// ================================
// Control Properties Classes
// ================================


/// ControlFlowProperties - Flow-level 제어 속성
type ControlFlowProperties() =
    inherit PropertiesBase<ControlFlowProperties>()

    // ========== Flow 레벨 제어 설정 ==========
    member val FlowControlEnabled = false with get, set

/// ControlSystemProperties - HW 디바이스 제어 및 HMI 연동
type ControlSystemProperties() =
    inherit PropertiesBase<ControlSystemProperties>()

    // ========== HMI 요소 관리 ==========
    // NOTE: HmiButtonIds, HmiLampIds, HmiConditionIds, HmiActionIds 제거됨
    // (array는 MemberwiseClone shallow copy 위험)
    // → 필요시 별도 컬렉션으로 관리 (예: DsSystem.HmiButtons: ResizeArray<Guid>)

    // ========== 자동 태그 생성 설정 ==========
    member val EnableAutoTagGeneration = false with get, set
    member val TagPrefix: string option = None with get, set
    member val TagNamingFormat = "{SystemId}_{WorkId}_{Signal}" with get, set

    // ========== 안전 설정 ==========
    member val EnableEmergencyStop = true with get, set
    member val EnableSafetyInterlock = true with get, set
    member val SafetyCheckTimeout = TimeSpan.FromSeconds(5.0) with get, set

    // ========== PLC 벤더 정보 (태그 매핑 전략) ==========
    member val PlcVendor: string option = None with get, set  // "Mitsubishi" | "Siemens" | "AB"
    member val PlcModel: string option = None with get, set   // "Q Series" | "S7-1200" | "CompactLogix"

/// ControlWorkProperties - Work-level 디바이스 제어 속성
type ControlWorkProperties() =
    inherit PropertiesBase<ControlWorkProperties>()

    // ========== 디바이스 정보 ==========
    member val DeviceName: string option = None with get, set
    member val DeviceType: string option = None with get, set  // "Cylinder" | "Motor" | "Valve" | "Gripper" | "Conveyor" | "Robot"
    member val ControlMode = "Auto" with get, set              // "Auto" | "Manual" | "Simulation"

    // ========== I/O 태그 매핑 (핵심) ==========
    member val StartTag: string option = None with get, set
    member val DoneTag: string option = None with get, set
    member val ErrorTag: string option = None with get, set
    member val ResetTag: string option = None with get, set

    // ========== 타이밍 제어 ==========
    member val StartDelay = TimeSpan.Zero with get, set
    member val Timeout = TimeSpan.FromSeconds(30.0) with get, set
    member val RetryCount = 0 with get, set
    member val RetryDelay = TimeSpan.FromSeconds(1.0) with get, set

    // ========== 위치 모니터링 ==========
    member val EnablePositionMonitoring = false with get, set
    member val PositionTag: string option = None with get, set
    member val ExpectedPosition: float option = None with get, set
    member val PositionTolerance = 0.0 with get, set

/// ControlCallProperties - Call-level 명령 제어 속성
type ControlCallProperties() =
    inherit PropertiesBase<ControlCallProperties>()

    // ========== 명령 제어 ==========
    member val CommandType: string option = None with get, set  // "Digital" | "Analog" | "Motion"
    member val CommandValue: string option = None with get, set
    member val CommandTag: string option = None with get, set

    // ========== 응답 검증 ==========
    member val ResponseType: string option = None with get, set
    member val ResponseTag: string option = None with get, set
    member val ExpectedResponse: string option = None with get, set
    member val ResponseTimeout = TimeSpan.FromSeconds(2.0) with get, set

    // ========== 펄스 제어 (스테퍼 모터 등) ==========
    member val UsePulseControl = false with get, set
    member val PulseWidth = 100 with get, set      // ms
    member val PulseInterval = 200 with get, set   // ms

    // ========== 모션 제어 ==========
    member val IsMotionControl = false with get, set
    member val TargetPosition: float option = None with get, set  // mm, pulse
    member val TargetVelocity: float option = None with get, set  // mm/s, rpm
    member val Acceleration: float option = None with get, set    // mm/s²
    member val Deceleration: float option = None with get, set    // mm/s²
