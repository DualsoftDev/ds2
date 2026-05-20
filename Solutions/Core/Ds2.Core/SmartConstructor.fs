namespace Ds2.Core.SmartCtor

open Ds2.Core

/// <summary>v10 spec §8 — Smart Constructor DSL.
/// canonical pair: standard / edge / SR latch / post-event stabilization / virtual gate.</summary>
module Action =
    let normal       = ActionType.Real (Level,   None)
    let pulse        = ActionType.Real (OneShot, None)
    let set          = ActionType.Real (Latched, None)
    let timeAppend ms= ActionType.Real (Level,   Some (Append ms))
    let pulseHold ms = ActionType.Real (OneShot, Some (Append ms))
    let virt         = ActionType.Virtual None
    let virtPlus ms  = ActionType.Virtual (Some (Append ms))

module Sensing =
    let normal       = SensingType.Real (Level,   None)
    let edge         = SensingType.Real (OneShot, None)
    let latched      = SensingType.Real (Latched, None)
    let debounce ms  = SensingType.Real (Level,   Some (Append ms))
    let edgeStable ms= SensingType.Real (OneShot, Some (Append ms))
    let virt         = SensingType.Virtual None
    let virtPlus ms  = SensingType.Virtual (Some (Append ms))
