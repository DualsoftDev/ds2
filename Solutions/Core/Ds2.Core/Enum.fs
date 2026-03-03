namespace Ds2.Core

/// Arrow relation semantics between nodes.
type ArrowType =
    | None        = 0
    | Start       = 1
    | Reset       = 2
    | StartReset  = 3
    | ResetReset  = 4
    | Group       = 5

/// Condition type for CallCondition entries.
type CallConditionType =
    | Auto   = 0
    | Common = 1
    | Active = 2

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
