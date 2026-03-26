namespace Ds2.IOList

open System
open Ds2.Core

// =============================================================================
// IEC 61131-3 Data Types
// =============================================================================

/// IEC 61131-3 standard data types
type IecDataType =
    | BOOL
    | SINT    // Int8
    | INT     // Int16
    | DINT    // Int32
    | LINT    // Int64
    | USINT   // UInt8
    | UINT    // UInt16
    | UDINT   // UInt32
    | ULINT   // UInt64
    | REAL    // Float32
    | LREAL   // Float64
    | STRING

module IecDataType =

    /// Convert DS2 ValueSpec to IEC data type
    let fromValueSpec (spec: ValueSpec) : IecDataType =
        match spec with
        | UndefinedValue   -> BOOL  // Default to BOOL
        | BoolValue _      -> BOOL
        | Int8Value _      -> SINT
        | Int16Value _     -> INT
        | Int32Value _     -> DINT
        | Int64Value _     -> LINT
        | UInt8Value _     -> USINT
        | UInt16Value _    -> UINT
        | UInt32Value _    -> UDINT
        | UInt64Value _    -> ULINT
        | Float32Value _   -> REAL
        | Float64Value _   -> LREAL
        | StringValue _    -> STRING

    /// Convert IEC data type to string
    let toString (dataType: IecDataType) : string =
        match dataType with
        | BOOL   -> "BOOL"
        | SINT   -> "SINT"
        | INT    -> "INT"
        | DINT   -> "DINT"
        | LINT   -> "LINT"
        | USINT  -> "USINT"
        | UINT   -> "UINT"
        | UDINT  -> "UDINT"
        | ULINT  -> "ULINT"
        | REAL   -> "REAL"
        | LREAL  -> "LREAL"
        | STRING -> "STRING"

// =============================================================================
// Template Types
// =============================================================================

/// Template slot entry - 실제 신호 또는 빈 슬롯
type TemplateSlot =
    /// 실제 신호 (ApiDefName, Pattern)
    /// 예: ("HOME_POS", "W_$(F)_I_$(D)_HOME_POS")
    | Signal of ApiDefName: string * Pattern: string
    /// 빈 슬롯 (주소는 소비하지만 신호는 생성하지 않음)
    | Empty

/// Memory area type
type MemoryArea =
    | InputWord    // IW
    | OutputWord   // QW
    | MemoryWord   // MW

/// Macro template for a specific system type
type MacroTemplate = {
    /// System type identifier (e.g., "RBT", "CONV")
    SystemType: string
    /// Category name
    Category: string
    /// Input word base address (default)
    IW_BaseAddress: int
    /// Output word base address (default)
    QW_BaseAddress: int
    /// Memory word base address (default)
    MW_BaseAddress: int
    /// Input signals in order (includes Empty slots)
    InputSlots: TemplateSlot list
    /// Output signals in order (includes Empty slots)
    OutputSlots: TemplateSlot list
    /// Memory signals in order (includes Empty slots)
    MemorySlots: TemplateSlot list
}

// =============================================================================
// Signal Generation Types
// =============================================================================

/// Generated signal record
type SignalRecord = {
    /// Variable name (e.g., "W_S131_I_RBT3_HOME_POS")
    VarName: string
    /// Data type (e.g., "BOOL")
    DataType: string
    /// PLC address (e.g., "%IW3070.0")
    Address: string
    /// IO type (IW, QW, MW)
    IoType: string
    /// Category (e.g., "RBT")
    Category: string
    /// Optional comment
    Comment: string option
    /// Flow name (for TAG Wizard mapping)
    FlowName: string
    /// Work name (for TAG Wizard mapping)
    WorkName: string
    /// Call name (for TAG Wizard mapping)
    CallName: string
    /// Device name (for TAG Wizard mapping)
    DeviceName: string
}

/// Generation context for a single ApiCall
type GenerationContext = {
    /// ApiCall ID
    ApiCallId: Guid
    /// ApiDef name (e.g., "HOME_POS")
    ApiDefName: string
    /// System type (e.g., "RBT")
    SystemType: string
    /// Flow name (extracted from OriginFlowId)
    FlowName: string
    /// Work name (extracted from parent Call's Work)
    WorkName: string
    /// Call name (extracted from parent Call)
    CallName: string
    /// Device alias (from Call.DevicesAlias)
    DeviceAlias: string
    /// Has input tag
    HasInputTag: bool
    /// Has output tag
    HasOutputTag: bool
    /// Input data type (from ApiCall.InputSpec)
    InputDataType: IecDataType
    /// Output data type (from ApiCall.OutputSpec)
    OutputDataType: IecDataType
}

// =============================================================================
// Result Types
// =============================================================================

/// Generation result
type GenerationResult = {
    /// Successfully generated IO signals
    IoSignals: SignalRecord list
    /// Successfully generated Dummy signals
    DummySignals: SignalRecord list
    /// Errors encountered during generation
    Errors: GenerationError list
    /// Warnings
    Warnings: string list
}

/// Generation error
and GenerationError = {
    /// ApiCall ID that caused the error
    ApiCallId: Guid option
    /// Error message
    Message: string
    /// Error type
    ErrorType: ErrorType
}

/// Error classification
and ErrorType =
    | TemplateNotFound of SystemType: string
    | ApiDefNotInTemplate of ApiDefName: string * SystemType: string
    | MissingOriginFlowId of ApiCallId: Guid
    | MissingSystemType of SystemId: Guid
    | MissingApiDefId of ApiCallId: Guid
    | ApiDefNotFound of ApiDefId: Guid
    | SystemNotFound of SystemId: Guid
    | FlowNotFound of FlowId: Guid
    | ParentCallNotFound of ApiCallId: Guid
    | InvalidTemplateFormat of FilePath: string * Reason: string

// =============================================================================
// Helper Functions
// =============================================================================

module SignalRecord =
    /// Create an IO signal record with data type
    let createIo varName address ioType category (dataType: IecDataType) flowName workName callName deviceName =
        {
            VarName = varName
            DataType = IecDataType.toString dataType
            Address = address
            IoType = ioType
            Category = category
            Comment = None
            FlowName = flowName
            WorkName = workName
            CallName = callName
            DeviceName = deviceName
        }

    /// Create a Dummy signal record with data type
    let createDummy varName address category (dataType: IecDataType) flowName workName callName deviceName =
        {
            VarName = varName
            DataType = IecDataType.toString dataType
            Address = address
            IoType = "MW"
            Category = category
            Comment = None
            FlowName = flowName
            WorkName = workName
            CallName = callName
            DeviceName = deviceName
        }

module GenerationError =
    /// Create a template not found error
    let templateNotFound systemType =
        {
            ApiCallId = None
            Message = $"Template not found for SystemType: {systemType}"
            ErrorType = TemplateNotFound systemType
        }

    /// Create an ApiDef not in template error
    let apiDefNotInTemplate apiCallId apiDefName systemType =
        {
            ApiCallId = Some apiCallId
            Message = $"ApiDef '{apiDefName}' not found in template for SystemType '{systemType}'"
            ErrorType = ApiDefNotInTemplate(apiDefName, systemType)
        }

    /// Create a missing OriginFlowId error
    let missingOriginFlowId apiCallId =
        {
            ApiCallId = Some apiCallId
            Message = $"ApiCall {apiCallId} has no OriginFlowId (data integrity issue)"
            ErrorType = MissingOriginFlowId apiCallId
        }

    /// Create a missing SystemType error
    let missingSystemType systemId =
        {
            ApiCallId = None
            Message = $"System {systemId} has no SystemType defined"
            ErrorType = MissingSystemType systemId
        }

module GenerationResult =
    /// Create an empty result
    let empty = {
        IoSignals = []
        DummySignals = []
        Errors = []
        Warnings = []
    }

    /// Combine multiple results
    let combine (results: GenerationResult list) =
        {
            IoSignals = results |> List.collect (fun r -> r.IoSignals)
            DummySignals = results |> List.collect (fun r -> r.DummySignals)
            Errors = results |> List.collect (fun r -> r.Errors)
            Warnings = results |> List.collect (fun r -> r.Warnings)
        }
