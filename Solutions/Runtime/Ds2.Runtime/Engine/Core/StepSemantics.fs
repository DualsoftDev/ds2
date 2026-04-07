namespace Ds2.Runtime.Engine.Core

open System
open Ds2.Core
open Ds2.Runtime.Model

module StepSemantics =

    let private normalizeSelectedSourceGuid (index: SimIndex) (selectedSourceGuid: Guid) =
        if selectedSourceGuid = Guid.Empty then None
        else Some (SimIndex.canonicalWorkGuid index selectedSourceGuid)

    let primableSourceGuids
        (index: SimIndex)
        (state: SimState)
        (getWorkState: Guid -> Status4)
        (autoStartSources: bool)
        (selectedSourceGuid: Guid)
        =
        let isPrimable workGuid =
            getWorkState workGuid = Status4.Ready
            && WorkConditionChecker.canStartWorkPredOnly index state workGuid

        if autoStartSources then
            index.TokenSourceGuids |> List.filter isPrimable
        else
            match normalizeSelectedSourceGuid index selectedSourceGuid with
            | Some workGuid when SimIndex.isTokenSource index workGuid && isPrimable workGuid -> [ workGuid ]
            | _ -> []

    let canAdvanceStep
        (index: SimIndex)
        (state: SimState)
        (getWorkState: Guid -> Status4)
        (hasStartableWork: bool)
        (hasActiveDuration: bool)
        (hasGoingCall: bool)
        (autoStartSources: bool)
        (selectedSourceGuid: Guid)
        =
        not hasGoingCall
        && (
            hasStartableWork
            || hasActiveDuration
            || not (primableSourceGuids index state getWorkState autoStartSources selectedSourceGuid |> List.isEmpty)
        )
