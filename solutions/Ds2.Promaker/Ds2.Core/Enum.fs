namespace Ds2.Core

// ─── Enumerations ───
// ArrowType, CallConditionType → Ds2.Core.Contracts/Enum.fs

/// Work와 Call의 실행 상태
type Status4 =
    | Ready  = 0  // 대기
    | Going  = 1  // 실행 중
    | Finish = 2  // 완료
    | Homing = 3  // 복귀

/// Call의 실행 타입
type CallType =
    | WaitForCompletion = 0  // 기본 타입: Call을 수행하고 Action 결과를 기다림
    | SkipIfCompleted   = 1  // Action이 이미 완료되어 있으면 수행한 것으로 간주
