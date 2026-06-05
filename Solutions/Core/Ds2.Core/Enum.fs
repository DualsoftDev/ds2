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


/// v10 §4 — 신호 모드 (cardinality 3).
type SignalMode =
    | Level                        // 조건 ON 동안 출력 ON / 신호 ON 동안 인정. coil / contact 매핑.
    | OneShot                      // 1 scan rising edge / 0→1 transition 1 scan. R_TRIG / OS 매핑.
    | Latched                      // 1샷 SET, 명시 RST 까지 hold / 0→1 transition 후 메모리 latch. -(S)- / SR flip-flop.

/// v10 §5 — 시간 정책 (cardinality 1 · Append only).
/// v9 의 TimeTotal 폐기 — "강제 시간 출력" 패턴은 Work.Duration 으로 표현.
type TimePolicy =
    | Append of ms: int            // 센서 도달 후 +ms 추가 출력 / 신호 안정 ms / Duration + ms 대기 (위치별 의미 분기).
    member this.Ms = match this with Append n -> n

/// v10 §3.2 — ApiDef 출력 인터페이스 특성 (D1 · WHEN · OUT).
/// 디바이스 내부 동작 시간 (Work.Duration) 과 완전히 무관 (다른 차원).
/// SensingType 과 동일 구조 대칭 (C-3.1).
type ActionType =
    | Real of SignalMode * TimePolicy option   // SignalMode 출력 + 선택적 TimePolicy.
    | Virtual of TimePolicy option             // 출력 없음 (NoOp) + 선택적 Append 대기.

/// v10 §3.2 — ApiDef 감지 인터페이스 특성 (D3 · WHEN · IN).
/// ActionType 과 동일 구조 — case 분기 시 dispatch만 다름 (C-3.1).
type SensingType =
    | Real of SignalMode * TimePolicy option   // SignalMode 감지 + 선택적 TimePolicy.
    | Virtual of TimePolicy option             // Duration 시점 대기 + 선택적 Append.
