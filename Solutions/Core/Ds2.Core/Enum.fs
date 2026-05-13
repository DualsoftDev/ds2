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

/// Per-leaf contact kind in CallCondition (LadderEditor visual ↔ store round-trip).
type ContactKind =
    | NoContact    = 0   // ─┤├─
    | NcContact    = 1   // ─┤/├─
    | RisingPulse  = 2   // ─┤P├─
    | FallingPulse = 3   // ─┤N├─
    | Inverter     = 4   // ──*──  (placeholder leaf — ApiCallId ignored)

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


/// ApiDef 출력 인터페이스 특성 — "버튼을 어떻게 누를 것인가" 만 결정.
/// 디바이스 내부 동작 시간 (Work.Duration) 과는 완전히 무관 (다른 차원).
/// 완료 판정 (ApiCall.SkipInputSensor) 과도 무관.
/// 인자 의미: TimeTotal/TimeAppend = 출력 유지 ms, MultiAction = (반복 횟수, 간격 ms).
type ApiDefActionType =
    | Normal                       // 조건 ON 동안 출력 ON (센서 감지 시 OFF)
    | Push                         // SET latch — 다음 명령이 올 때까지 유지
    | Pulse                        // Rising edge 시 1 scan 펄스
    | TimeTotal of int             // 지정 시간(ms)만큼 절대 출력 ON (센서·내부 시간 무관)
    | TimeAppend of int            // 센서 감지 후 추가 N ms 출력 유지 (위치 고정 / 정밀도 향상)
    | MultiAction of int * int     // (count, intervalMs) — N회 · 간격 ms 로 출력 ON 반복
