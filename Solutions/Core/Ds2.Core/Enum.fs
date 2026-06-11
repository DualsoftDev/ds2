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

/// Condition type for Condition entries (shared by Call/Work).
type ConditionType =
    | AutoAux    = 0
    | ComAux     = 1
    | SkipAction = 2

/// 외부 DLL(AAStoPLC) binary 호환용 — int 값은 `ConditionType` 과 동일.
/// 신규 코드에서는 `ConditionType` 사용. 외부 DLL 재빌드 시 제거 예정.
type CallConditionType =
    | AutoAux    = 0
    | ComAux     = 1
    | SkipAction = 2

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

/// Call 이 Work 내 실행 시퀀스에서 차지하는 위치 라벨.
/// DsPilot·모니터링은 Call 화살표 토폴로지 없이 이 라벨만으로 사이클 시작(Head)·종료(Tail)를 판정한다.
/// Promaker 는 모델 로드 시 화살표 연결정보로부터 자동 지정한다 (UI 편집 없음).
type SequenceLabel =
    | Body = 0   // 시퀀스 중간 (기본값)
    | Head = 1   // 진입점 — 사이클 시작
    | Tail = 2   // 종료점 — 사이클 종료

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


/// v16 — ApiDef 출력(Action) 인터페이스 특성. ApiDefType × TimeOption 매트릭스의 Action 측 유효 조합만 표현.
/// 불법 조합(Latch+T, Virtual+T)은 타입으로 차단 — "설정시 예외발생" 상황 자체가 없다.
/// 디바이스 내부 동작 시간(Work.Duration)과 무관한 별개 차원.
[<RequireQualifiedAccess>]
type ActionType =
    | Normal of timeMs: int option   // None: 센서 감지 완료까지 출력 유지 / Some T: 감지 후 T(ms) 연장 유지 후 off.
    | Pulse of timeMs: int option    // None: 1 scan 펄스 / Some T: T(ms) 유지 후 off.
    | Latch                          // SET — 같은 Device 의 다른 Api 호출 때까지 유지 (mutex 해제).
    | Virtual                        // 출력 없음 (주로 Latch 출력의 상대 동작으로 사용).

/// v16 — ApiDef 감지(Sensing) 인터페이스 특성. 매트릭스의 Sensor 측 유효 조합만 표현.
/// Pulse 감지는 존재하지 않음. Latch/Virtual 은 T 필수.
[<RequireQualifiedAccess>]
type SensingType =
    | Normal of timeMs: int option   // None: 감지 즉시 완료 / Some T: 감지 후 T(ms) 지연 완료 — T 중 off(채터링) = 완료 취소 + SensorOff abnormal.
    | Latch of timeMs: int           // 감지 후 T(ms) 지연 완료 — T 구간 채터링 허용 (감지 latch).
    | Virtual of timeMs: int         // 출력 발생 시점 + T(ms) 후 완료 — 설비에 센서가 없을 때 사용.
