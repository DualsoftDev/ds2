namespace Ds2.Core

// ─── Enumerations ───

/// Work와 Call의 실행 상태
type Status4 =
    | Ready  = 0  // 대기
    | Going  = 1  // 실행 중
    | Finish = 2  // 완료
    | Homing = 3  // 복귀

/// Arrow의 인과 관계 타입
type ArrowType =
    | None        = 0  // 연결 없음
    | Start       = 1  // 시작 트리거 (source 완료 시 target 시작)
    | Reset       = 2  // 리셋 트리거 (source 시작 시 target 리셋)
    | StartReset  = 3  // 시작+리셋 (source 완료 시 target 시작 + target 시작 시 source 리셋)
    | ResetReset  = 4  // 리셋+리셋 (source 시작 시 target 리셋 + target 시작 시 source 리셋)
    | Group       = 5  // 그룹 연결

/// Call의 실행 타입
type CallType =
    | WaitForCompletion = 0  // 기본 타입: Call을 수행하고 Action 결과를 기다림
    | SkipIfCompleted   = 1  // Action이 이미 완료되어 있으면 수행한 것으로 간주

/// CallCondition의 타입
type CallConditionType =
    | Auto   = 0  // AutoConditions
    | Common = 1  // CommonConditions
    | Active = 2  // ActiveTriggers
