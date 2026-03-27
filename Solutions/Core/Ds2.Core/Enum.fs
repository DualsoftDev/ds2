namespace Ds2.Core

/// Arrow relation semantics between nodes.
type ArrowType =
    | Unspecified = 0
    | Start       = 1
    | Reset       = 2
    | StartReset  = 3
    | ResetReset  = 4
    | Group       = 5

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
