namespace Ds2.Core.SmartCtor

open Ds2.Core

/// <summary>v16 Smart Constructor DSL — ApiDefType(Normal/Pulse/Latch/Virtual) × TimeOption 유효 조합.</summary>
module Action =
    let normal        = ActionType.Normal None
    let normalHold ms = ActionType.Normal (Some ms)   // 센서 감지 후 ms 연장 유지
    let pulse         = ActionType.Pulse None
    let pulseHold ms  = ActionType.Pulse (Some ms)    // ms 유지 후 off
    let latch         = ActionType.Latch              // 다른 Api 호출까지 유지
    let virt          = ActionType.Virtual            // 출력 없음 (Latch 상대 동작용)

module Sensing =
    let normal       = SensingType.Normal None
    let stable ms    = SensingType.Normal (Some ms)   // 감지 후 ms 지연 완료 — 채터링 = 완료취소 + abnormal
    let latch ms     = SensingType.Latch ms           // 감지 후 ms 지연 완료 — 채터링 허용
    let virt ms      = SensingType.Virtual ms         // 출력 시점 + ms 완료 (센서 없는 설비)
