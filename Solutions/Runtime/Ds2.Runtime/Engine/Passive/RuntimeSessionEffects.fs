namespace Ds2.Runtime.Engine.Passive

open System
open Ds2.Core
open Ds2.Runtime.Engine.Core

type RuntimeHubEffectKind =
    | Log = 0
    | InjectIoByAddress = 1
    | ForceWorkState = 2
    | WriteTag = 3
    | PassiveObserve = 4

type RuntimeHubLogSeverity =
    | Info = 0
    | System = 1
    | Warn = 2
    | Going = 3
    | Finish = 4
    | Ready = 5
    | Homing = 6

[<CLIMutable>]
type RuntimeHubEffect = {
    Kind: RuntimeHubEffectKind
    DelayMs: int
    Message: string
    Severity: RuntimeHubLogSeverity
    Address: string
    Value: string
    WorkGuid: Guid
    State: Status4
}

[<RequireQualifiedAccess>]
module RuntimeSessionEffects =
    let addEffect
        (effects: ResizeArray<RuntimeHubEffect>)
        kind
        delayMs
        message
        severity
        address
        value
        workGuid
        state =
        effects.Add({
            Kind = kind
            DelayMs = delayMs
            Message = message
            Severity = severity
            Address = address
            Value = value
            WorkGuid = workGuid
            State = state
        })

    let addLog effects delayMs severity message =
        addEffect effects RuntimeHubEffectKind.Log delayMs message severity "" "" Guid.Empty Status4.Ready

    let addInjectIo effects address value =
        addEffect effects RuntimeHubEffectKind.InjectIoByAddress 0 "" RuntimeHubLogSeverity.Info address value Guid.Empty Status4.Ready

    let addForceWorkState effects delayMs workGuid state =
        addEffect effects RuntimeHubEffectKind.ForceWorkState delayMs "" RuntimeHubLogSeverity.Info "" "" workGuid state

    let addWriteTag effects delayMs address value =
        addEffect effects RuntimeHubEffectKind.WriteTag delayMs "" RuntimeHubLogSeverity.Info address value Guid.Empty Status4.Ready

    let addPassiveObserve effects delayMs address value =
        addEffect effects RuntimeHubEffectKind.PassiveObserve delayMs "" RuntimeHubLogSeverity.Info address value Guid.Empty Status4.Ready
