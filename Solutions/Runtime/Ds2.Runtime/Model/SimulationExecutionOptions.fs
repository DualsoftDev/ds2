namespace Ds2.Runtime.Engine

open System
open Ds2.Core

/// Initialization policy is independent of IO/runtime mode; old engines retain inference.
type SimulationInitializationPolicy =
    | LegacyAutoHoming = 0
    | ModelOnly = 1

type SimulationRearmingPolicy =
    | LegacyDeviceRecovery = 0
    | ResetEdgesOnly = 1

/// Immutable, session-scoped execution choices. Only the two complete contracts below
/// are supported; mixed policies would imply an untested third execution contract.
/// ModelOnly uses positive IsFinished flags, with Ready as the default. It never infers
/// initial state or homing commands from Call order or Work names.
/// ResetEdgesOnly retains normal Work reset edges and child Call lifecycle/epoch guards;
/// it disables automatic mutual-Finish device recovery. It does not add Call reset edges.
[<Sealed>]
type SimulationExecutionOptions(initialization: SimulationInitializationPolicy, rearming: SimulationRearmingPolicy) =
    do
        match initialization, rearming with
        | SimulationInitializationPolicy.LegacyAutoHoming, SimulationRearmingPolicy.LegacyDeviceRecovery
        | SimulationInitializationPolicy.ModelOnly, SimulationRearmingPolicy.ResetEdgesOnly -> ()
        | _ -> invalidArg "initialization" "Unsupported simulation initialization/rearming policy combination."

    member _.Initialization = initialization
    member _.Rearming = rearming

    static member Legacy =
        SimulationExecutionOptions(SimulationInitializationPolicy.LegacyAutoHoming, SimulationRearmingPolicy.LegacyDeviceRecovery)

    static member Explicit =
        SimulationExecutionOptions(SimulationInitializationPolicy.ModelOnly, SimulationRearmingPolicy.ResetEdgesOnly)

    member internal _.ValidateRuntimeMode(runtimeMode: RuntimeMode) =
        if initialization = SimulationInitializationPolicy.ModelOnly && runtimeMode <> RuntimeMode.Simulation then
            invalidArg "runtimeMode" "Explicit initialization/rearming is supported only in Simulation mode."
