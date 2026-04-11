namespace Ds2.Core

open System.Reflection

/// Arrow relation semantics between nodes.
type ArrowType =
    | Unspecified = 0   // 연결 없음
    | Start       = 1   // 시작 트리거 (source 완료 시 target 시작)
    | Reset       = 2   // 리셋 트리거 (source 시작 시 target 리셋)
    | StartReset  = 3   // 시작+리셋 (source 완료 시 target 시작 + target 시작 시 source 리셋)
    | ResetReset  = 4   // 리셋+리셋 (source 시작 시 target 리셋 + target 시작 시 source 리셋)
    | Group       = 5   // 그룹 연결

/// Condition type for CallCondition entries.
type CallConditionType =
    | AutoAux      = 0
    | ComAux       = 1
    | SkipUnmatch  = 2

/// Runtime status for Work/Call.
type Status4 =
    | Ready  = 0
    | Going  = 1
    | Finish = 2
    | Homing = 3

/// Execution mode for Call.
type CallType =
    | WaitForCompletion = 0
    | SkipIfCompleted   = 1

/// Token role for Work in DataToken simulation.
[<System.Flags>]
type TokenRole =
    | None   = 0
    | Source = 1
    | Ignore = 2
    | Sink   = 4

/// Flow runtime state tag for step-by-step simulation.
type FlowTag =
    | Ready = 0
    | Drive = 1
    | Pause = 2

type IOTagDataType =
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

/// Runtime execution mode.
type RuntimeMode =
    | Simulation   = 0  // RGFH 상태 전이만 처리 (가상 시뮬레이션)
    | Control      = 1  // IO 실제 읽기/쓰기 (PLC 제어)
    | Monitoring   = 2  // IO 읽어서 RGFH 상태 추적 (모니터링)
    | VirtualPlant = 3  // 외부 출력 받아서 외부로 입력값 써주기 (가상 플랜트)


type ApiDefActionType =
    | Normal 
    | Push   
    | Pulse  
    | Time of int  // Time-based action with specified duration in milliseconds
