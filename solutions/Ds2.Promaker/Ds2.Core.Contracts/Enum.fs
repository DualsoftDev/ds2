namespace Ds2.Core

/// Arrow의 인과 관계 타입
type ArrowType =
    | None        = 0  // 연결 없음
    | Start       = 1  // 시작 트리거 (source 완료 시 target 시작)
    | Reset       = 2  // 리셋 트리거 (source 시작 시 target 리셋)
    | StartReset  = 3  // 시작+리셋 (source 완료 시 target 시작 + target 시작 시 source 리셋)
    | ResetReset  = 4  // 리셋+리셋 (source 시작 시 target 리셋 + target 시작 시 source 리셋)
    | Group       = 5  // 그룹 연결

/// CallCondition의 타입
type CallConditionType =
    | Auto   = 0  // AutoConditions
    | Common = 1  // CommonConditions
    | Active = 2  // ActiveTriggers
